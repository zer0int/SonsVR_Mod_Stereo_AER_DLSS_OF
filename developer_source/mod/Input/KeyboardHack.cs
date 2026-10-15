using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SonsVRMod
{
    /// <summary>
    /// Emulates traditional keyboard/mouse input based on VR controller gestures.
    /// This allows VR hand movements to control in-game actions like movement, jumping, melee attacks, etc.
    /// </summary>
    public class KeyboardHack
    {
        // Jump-related state and constants
        private float _jumpCooldown = 0f;
        private const float JUMP_THRESHOLD = 1.5f;    // Sensitivity threshold for jump detection (m/s)
        private const float JUMP_DELAY = 0.8f;        // Minimum time (in seconds) between jump gestures

        // Melee attack state and constants
        private bool _isMeleeAttacking = false;
        private float _attackCooldown = 0f;
        private const float ATTACK_THRESHOLD = 3.5f;        // Hand speed required to trigger an attack (m/s)
        private const float RELEASE_THRESHOLD = 1.2f;       // Speed below which the attack button is released
        private const float MIN_ATTACK_INTERVAL = 0.2f;     // Minimum time between consecutive melee swings

        
        private bool _isGripPressed = false;
        private float _gripPressStartTime = 0f;
        private const float LongPressThreshold = 2f; // Long press duration in seconds

        public GameObject LocalPlayer;
        private bool _wasRightGripRightPressed = false;
        private bool _wasLeftGripRightPressed = false;
        private bool _wasBGripRightPressed = false;


        /// <summary>
        /// Main entry point called each frame to inject simulated keyboard and mouse input
        /// based on current VR controller states.
        /// </summary>
        public void InjectMovement()
        {
            // Retrieve reference to Unity's virtual keyboard device
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            
            // Create a synthetic StateEvent that mimics real keyboard input
            StateEvent.From(keyboard, out var eventPtr);
            
            
            // Read current thumbstick values from VR controllers
            UnityEngine.Vector2 lstick = NativeVRInput.GetVector2("ThumbstickLeft");
            UnityEngine.Vector2 rstick = NativeVRInput.GetVector2("ThumbstickRight");

            // Read grip and face button states
            bool leftGripPress = NativeVRInput.GetBoolean("GripLeft_Press");
            bool rightGripPress = NativeVRInput.GetBoolean("GripRight_Press");
            bool currentGripPressed = leftGripPress || rightGripPress;
            if (currentGripPressed && !_isGripPressed)
            {
                // Grip just pressed: start timer
                _isGripPressed = true;
                _gripPressStartTime = Time.time;
    
                // Emulate 'E' key press
                keyboard.eKey.WriteValueIntoEvent(1f, eventPtr);
            }
            else if (!currentGripPressed && _isGripPressed)
            {
                // Grip released
                _isGripPressed = false;
                float pressDuration = Time.time - _gripPressStartTime;

                if (pressDuration >= LongPressThreshold)
                {
                    // Long press: emulate a quick tap of 'G' (press + immediate release)
                    keyboard.gKey.WriteValueIntoEvent(1f, eventPtr);
                    InputSystem.QueueEvent(eventPtr);

                    // Release 'G' immediately (simulate tap)
                    StateEvent.From(keyboard, out var releaseEvent);
                    keyboard.gKey.WriteValueIntoEvent(0f, releaseEvent);
                    InputSystem.QueueEvent(releaseEvent);
                }
                else
                {
                    // Short press: release 'E'
                    keyboard.eKey.WriteValueIntoEvent(0f, eventPtr);
                }
            }
            else if (currentGripPressed && _isGripPressed)
            {
                // Keep 'E' held down
                keyboard.eKey.WriteValueIntoEvent(1f, eventPtr);
            }
            
            bool YPress = NativeVRInput.GetBoolean("ButtonLeft_Y_Press");
            bool XPress = NativeVRInput.GetBoolean("ButtonLeft_X_Press");
            bool APress = NativeVRInput.GetBoolean("ButtonRight_A_Press");
            bool BPress = NativeVRInput.GetBoolean("ButtonRight_B_Press");

            
            // Map left thumbstick to WASD movement
            keyboard.wKey.WriteValueIntoEvent(lstick.y > 0.1f ? 1f : 0f, eventPtr);
            keyboard.sKey.WriteValueIntoEvent(lstick.y < -0.1f ? 1f : 0f, eventPtr);
            keyboard.aKey.WriteValueIntoEvent(lstick.x < -0.1f ? 1f : 0f, eventPtr);
            keyboard.dKey.WriteValueIntoEvent(lstick.x > 0.1f ? 1f : 0f, eventPtr);
            
            // Map Y button (left controller) to 'I' (e.g., inventory)
            keyboard.iKey.WriteValueIntoEvent(YPress ? 1f : 0f, eventPtr);
            
            // Map B button (right controller) to 'L' (e.g., LIGHTER)
            keyboard.lKey.WriteValueIntoEvent(BPress ? 1f : 0f, eventPtr);
            
            // Map A button (right controller) to 'R' (e.g., RELOAD/ROTATE RIGHT)
            keyboard.rKey.WriteValueIntoEvent(APress ? 1f : 0f, eventPtr); // FIXED: was BPress

            
            
            // Map right thumbstick down to Left Ctrl (e.g., crouch)
            keyboard.leftCtrlKey.WriteValueIntoEvent(rstick.y < -0.3f ? 1f : 0f, eventPtr);

            // Submit the synthetic event to Unity's input system
            InputSystem.QueueEvent(eventPtr);

            // Update gesture-based actions
            UpdateJumpGesture();
            UpdateMouseInjection();
            UpdateMeleeAttackGesture();
            UpdateWalkieTalkieInput();
            UpdateGpsInput();
            UpdateBookInput();
        }

        /// <summary>
        /// Simulates mouse button presses based on VR trigger inputs.
        /// Right trigger = left mouse button (attack/fire), left trigger = right mouse button (aim/block).
        /// </summary>
        public void UpdateMouseInjection()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            // Read trigger states (digital press detection)
            bool triggerRight = NativeVRInput.GetBoolean("TriggerRight_Press");   // Attack/fire
            bool triggerLeft = NativeVRInput.GetBoolean("TriggerLeft_Press");     // Aim/block

            // Generate a synthetic mouse event
            StateEvent.From(mouse, out var eventPtr);
            mouse.leftButton.WriteValueIntoEvent(triggerRight ? 1f : 0f, eventPtr);
            mouse.rightButton.WriteValueIntoEvent(triggerLeft ? 1f : 0f, eventPtr);
            InputSystem.QueueEvent(eventPtr);
        }

        /// <summary>
        /// Detects a "jump" gesture when both hands move upward rapidly.
        /// Requires both hands to exceed a vertical velocity threshold simultaneously.
        /// </summary>
        public void UpdateJumpGesture()
        {
            if (_jumpCooldown > 0)
            {
                _jumpCooldown -= Time.deltaTime;
                return;
            }

            // Get vertical (Y-axis) velocity of both hands
            float leftY = NativeVRInput.GetHandVelocity("PoseLeft").y;
            float rightY = NativeVRInput.GetHandVelocity("PoseRight").y;

            // Trigger jump only if both hands exceed the upward velocity threshold
            if (leftY > JUMP_THRESHOLD && rightY > JUMP_THRESHOLD)
            {
                MelonLogger.Msg($"[GESTURE] Jump detected! LeftVel: {leftY:F2} m/s, RightVel: {rightY:F2} m/s");
                ExecuteJumpAction();
                _jumpCooldown = JUMP_DELAY; // Prevent rapid successive jumps
            }
        }

        /// <summary>
        /// Detects and simulates melee swing gestures using right-hand speed.
        /// Presses the left mouse button on fast motion, releases it when motion slows.
        /// </summary>
        public void UpdateMeleeAttackGesture()
        {
            if (_attackCooldown > 0)
                _attackCooldown -= Time.deltaTime;

            // Get full 3D velocity of the right hand
            Vector3 velocity = NativeVRInput.GetHandVelocity("PoseRight");
            float currentSpeed = velocity.magnitude;

            var mouse = Mouse.current;
            if (mouse == null) return;

            // --- ATTACK TRIGGER ---
            // If hand is moving fast enough, not already attacking, and cooldown has passed
            if (currentSpeed > ATTACK_THRESHOLD && !_isMeleeAttacking && _attackCooldown <= 0)
            {
                _isMeleeAttacking = true;
                _attackCooldown = MIN_ATTACK_INTERVAL;

                StateEvent.From(mouse, out var eventPtr);
                mouse.leftButton.WriteValueIntoEvent(1f, eventPtr);
                InputSystem.QueueEvent(eventPtr);

                MelonLogger.Msg($"[COMBAT] Melee attack detected! Speed: {currentSpeed:F2} m/s");
            }

            // --- ATTACK RELEASE ---
            // Release the mouse button once hand slows below the release threshold
            else if (currentSpeed < RELEASE_THRESHOLD && _isMeleeAttacking)
            {
                _isMeleeAttacking = false;
                StateEvent.From(mouse, out var eventPtr);
                mouse.leftButton.WriteValueIntoEvent(0f, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        /// <summary>
        /// Executes a jump by simulating a momentary press of the spacebar.
        /// </summary>
        private void ExecuteJumpAction()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            // Press the space key
            StateEvent.From(keyboard, out var eventPtr);
            keyboard.spaceKey.WriteValueIntoEvent(1f, eventPtr);
            InputSystem.QueueEvent(eventPtr);

            // Schedule key release after a short delay
            MelonCoroutines.Start(ReleaseSpace(keyboard));
        }

        /// <summary>
        /// Coroutine that releases the spacebar after a brief duration to simulate a real key press.
        /// </summary>
        /// <param name="keyboard">Reference to the Unity virtual keyboard device.</param>
        /// <returns>An IEnumerator for use as a coroutine.</returns>
        private System.Collections.IEnumerator ReleaseSpace(Keyboard keyboard)
        {
            yield return new WaitForSeconds(0.1f);
            StateEvent.From(keyboard, out var eventPtr);
            keyboard.spaceKey.WriteValueIntoEvent(0f, eventPtr);
            InputSystem.QueueEvent(eventPtr);
        }
        
        public void ExecutePress_E()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            // Press the E key
            StateEvent.From(keyboard, out var eventPtr);
            keyboard.eKey.WriteValueIntoEvent(1f , eventPtr);
            InputSystem.QueueEvent(eventPtr);

            // Schedule key release after a short delay
            MelonCoroutines.Start(ReleaseE(keyboard));
        }

        /// <summary>
        /// Coroutine that releases the E key after a brief duration to simulate a real key press.
        /// </summary>
        /// <param name="keyboard">Reference to the Unity virtual keyboard device.</param>
        /// <returns>An IEnumerator for use as a coroutine.</returns>
        private System.Collections.IEnumerator ReleaseE(Keyboard keyboard)
        {
            yield return new WaitForSeconds(0.1f);
            StateEvent.From(keyboard, out var eventPtr);
            keyboard.eKey.WriteValueIntoEvent(0f , eventPtr);
            InputSystem.QueueEvent(eventPtr);
        }

        /// <summary>
        /// Simulates a real physical press of the gamepad's Start/Menu button - the same button
        /// that opens the pause menu. Fixes a GPU-underutilization/low-FPS issue that's only
        /// ever been confirmed fixed by a genuine menu open/close, not by anything faked purely
        /// in our own code. Unlike a synthetic volume-weight toggle, this is a real input event
        /// indistinguishable from a physical press, so whatever the game's own pause logic
        /// actually does (Time.timeScale, canvas, or anything else we don't have visibility
        /// into) runs exactly as it normally would.
        /// </summary>
        public void ExecutePress_StartButton()
        {
            var gamepad = Gamepad.current;
            if (gamepad == null) return;

            StateEvent.From(gamepad, out var eventPtr);
            gamepad.startButton.WriteValueIntoEvent(1f, eventPtr);
            InputSystem.QueueEvent(eventPtr);

            MelonCoroutines.Start(ReleaseStartButton(gamepad));
        }

        /// <summary>
        /// Releases the Start button shortly after pressing it, simulating a real quick press
        /// rather than a stuck-down button.
        /// </summary>
        private System.Collections.IEnumerator ReleaseStartButton(Gamepad gamepad)
        {
            yield return new WaitForSeconds(0.15f);
            StateEvent.From(gamepad, out var eventPtr);
            gamepad.startButton.WriteValueIntoEvent(0f, eventPtr);
            InputSystem.QueueEvent(eventPtr);
        }

        /// <summary>
        /// Full automated cycle: presses Start to open the pause menu (fixing the FPS issue),
        /// waits about a second, then presses B to close it again - same button the player
        /// would normally press manually. One self-contained routine so the open/close timing
        /// is handled reliably in one place rather than split across separate callers.
        /// </summary>
        public bool AutoMenuFixSucceeded { get; private set; } = false;
        private bool _autoMenuFixRunning = false;

        /// <summary>
        /// Allows the auto-menu FPS fix to retrigger on a genuinely new session (e.g. starting a
        /// new game after already having played one this process), rather than only ever running
        /// once per game launch. Does not interrupt a currently in-progress attempt.
        /// </summary>
        public void ResetAutoMenuFixState()
        {
            if (_autoMenuFixRunning) return;
            AutoMenuFixSucceeded = false;
        }

        public void ExecuteAutoOpenAndCloseMenu()
        {
            if (_autoMenuFixRunning || AutoMenuFixSucceeded) return;
            var gamepad = Gamepad.current;
            if (gamepad == null) return;

            _autoMenuFixRunning = true;
            _lastOwnBPressTime = -10f;
            _manualBPressDetected = false;
            MelonCoroutines.Start(AutoOpenAndCloseMenuRoutine());
            MelonCoroutines.Start(WatchForManualBPress());
        }

        // Per user's request: the menu was visibly open for over 2 seconds total (0.15s button
        // press/release x2, plus these two delays) - wants it down to "almost just a half second,
        // just a flash". Split into two separately-tunable delays since they serve different
        // purposes: OpenVerifyDelay is a technical check (confirming Time.timeScale genuinely
        // dropped before proceeding, i.e. the menu actually opened) and needs to stay long enough
        // for that to reliably register; HoldBeforeCloseDelay is purely cosmetic - "how long do we
        // leave it open once we already know it worked" - and can shrink much further.
        public static float AutoMenuFixOpenVerifyDelay = 0.25f;
        public static float AutoMenuFixHoldBeforeCloseDelay = 0.1f;

        // FIXED (per user's own idea - genuinely different approach from anything tried so far,
        // and doesn't depend on the unreliable canvas signal at all): the real problem with the
        // Time.timeScale-only version wasn't "no way to auto-close in multiplayer" - it was that
        // our loop had no way to know the PLAYER had already handled it themselves, so it kept
        // blindly retrying regardless. Watching for a genuine B-press from the player - not one of
        // our own synthetic ones, distinguished by a suppression window around exactly when we
        // inject our own - and treating that as "the player is handling this themselves, stop
        // entirely" sidesteps needing any signal at all for what state the menu is actually in.
        private float _lastOwnBPressTime = -10f;
        private bool _manualBPressDetected = false;

        private System.Collections.IEnumerator WatchForManualBPress()
        {
            while (_autoMenuFixRunning)
            {
                var gamepad = Gamepad.current;
                if (gamepad != null && gamepad.bButton.wasPressedThisFrame)
                {
                    // Ignore presses within a short window of our own injected one - this is us,
                    // not the player.
                    if (Time.unscaledTime - _lastOwnBPressTime > 0.75f)
                    {
                        _manualBPressDetected = true;
                        MelonLogger.Msg("[VRPlayer] Auto-menu FPS fix: detected the player's own B press - stopping, letting them handle it themselves from here.");
                        AutoMenuFixSucceeded = true;
                        _autoMenuFixRunning = false;
                        yield break;
                    }
                }
                yield return null; // every frame - a single-frame flag needs per-frame polling to catch reliably
            }
        }

        /// <summary>
        /// FIXED: the original version fired one Start-press, waited, then unconditionally
        /// pressed B and declared success - confirmed via user report that during the new-game
        /// intro sequence (helicopter cutscene -> an interactive window, of unpredictable length
        /// since it waits on the player reading a laptop rather than a fixed cutscene duration -
        /// where pause genuinely doesn't work -> crash cutscene -> gameplay), the game ignores
        /// pause input for most of that window even though MainCameraFP is already active and a
        /// gamepad is already detected. That combination silently burned the one-shot attempt
        /// with zero effect. Now verifying real success via Time.timeScale actually dropping to 0
        /// (the same signal already established in this file as proof the menu genuinely opened,
        /// since WaitForSecondsRealtime's whole purpose here is to keep counting real time
        /// through that paused state) before ever pressing B or declaring success - if the menu
        /// didn't actually open, retries every few seconds. Deliberately unbounded (no attempt
        /// cap) rather than giving up after some fixed time - the blocked window's length is
        /// entirely player-paced (however long someone spends on the laptop before closing it or
        /// skipping the cutscene), so any fixed cap risks giving up while the player is still
        /// legitimately in that window, leaving them stuck at low FPS for the rest of the
        /// session. The cost of an extra harmless, ignored button press every few seconds is
        /// negligible next to that risk.
        ///
        /// FIXED A SIXTH TIME (user's own idea, dropping the unreliable canvas signal entirely):
        /// the multiplayer fallback signal turned out capable of a false positive even in
        /// multiplayer itself, at the same cutscene-to-gameplay transition moment that caused
        /// trouble in single-player - confirmed via log (succeeded on attempt 1, ~6.8s in, yet the
        /// user confirmed the FPS issue genuinely persisted afterward). Rather than keep tuning
        /// delays around an already-proven-unreliable signal, back to Time.timeScale as the ONLY
        /// signal for "did it open" (never shown a false reading, ever) - and for multiplayer
        /// (where that never confirms at all), relying entirely on WatchForManualBPress above
        /// instead: if the player closes the menu themselves at any point, that's detected
        /// directly and the loop stops immediately, rather than trying to infer game state from a
        /// signal that's already proven it can lie.
        /// </summary>
        private System.Collections.IEnumerator AutoOpenAndCloseMenuRoutine()
        {
            int attempt = 0;
            while (true)
            {
                if (!_autoMenuFixRunning) yield break; // stopped by WatchForManualBPress

                attempt++;
                var gamepad = Gamepad.current;
                if (gamepad == null)
                {
                    yield return new WaitForSecondsRealtime(1f);
                    continue;
                }

                // Always safe to press Start - confirmed it only ever opens the menu, never
                // closes it, so there's no risk of accidentally closing an already-open menu here.
                StateEvent.From(gamepad, out var pressStart);
                gamepad.startButton.WriteValueIntoEvent(1f, pressStart);
                InputSystem.QueueEvent(pressStart);

                yield return new WaitForSecondsRealtime(0.15f);

                StateEvent.From(gamepad, out var releaseStart);
                gamepad.startButton.WriteValueIntoEvent(0f, releaseStart);
                InputSystem.QueueEvent(releaseStart);

                // Give the game a brief moment to actually process the press before checking -
                // real time, since this check needs to work whether or not timeScale has changed.
                yield return new WaitForSecondsRealtime(AutoMenuFixOpenVerifyDelay);

                if (!_autoMenuFixRunning) yield break; // stopped by WatchForManualBPress while waiting

                if (Time.timeScale > 0.01f)
                {
                    // Menu didn't actually open (per Time.timeScale) - almost certainly still
                    // inside the blocked window (single-player) or genuinely in multiplayer (where
                    // this will never confirm - WatchForManualBPress is what eventually stops this
                    // loop there, once the player closes whatever this opened). Wait and retry
                    // rather than give up - unbounded, since the blocked window's length is
                    // entirely player-paced. Only logging every 10th attempt (~30s at this
                    // interval) to avoid spam over a long, genuinely-expected wait.
                    if (attempt % 10 == 1)
                    {
                        MelonLogger.Msg($"[VRPlayer] Auto-menu FPS fix: Start press had no effect (attempt {attempt}) - game likely still in a cutscene/blocked state, or in multiplayer where this never confirms - retrying.");
                    }
                    yield return new WaitForSecondsRealtime(2f); // 1s above + 2s here = ~3s cadence
                    continue;
                }

                // Confirmed genuinely paused via Time.timeScale - the one signal that's never
                // shown a false reading. Hold briefly, then close it.
                yield return new WaitForSecondsRealtime(AutoMenuFixHoldBeforeCloseDelay);

                if (!_autoMenuFixRunning) yield break; // stopped by WatchForManualBPress while waiting

                _lastOwnBPressTime = Time.unscaledTime; // so WatchForManualBPress ignores this one
                StateEvent.From(gamepad, out var pressClose);
                gamepad.bButton.WriteValueIntoEvent(1f, pressClose);
                InputSystem.QueueEvent(pressClose);

                yield return new WaitForSecondsRealtime(0.15f);

                StateEvent.From(gamepad, out var releaseClose);
                gamepad.bButton.WriteValueIntoEvent(0f, releaseClose);
                InputSystem.QueueEvent(releaseClose);

                AutoMenuFixSucceeded = true;
                _autoMenuFixRunning = false;
                MelonLogger.Msg($"[VRPlayer] Automated pause-menu open+close cycle complete (succeeded on attempt {attempt}, confirmed via Time.timeScale).");
                PerformanceForensicsV106.NotifyExternalEvent($"AUTO_PAUSE_FIX_COMPLETE attempt={attempt}");
                yield break;
            }
        }
     
        
        public void UpdateWalkieTalkieInput()
        {
            if (LocalPlayer == null) return;

            // 1. Read input
            bool rightGripPress = NativeVRInput.GetBoolean("GripRight_Press");
            Vector3 rightHandPos = NativeVRInput.GetPose("PoseRight").position;
            

            // 2. Calculate left shoulder position
            Vector3 playerPos = LocalPlayer.transform.position;
            Vector3 leftShoulderPos = playerPos 
                                      + LocalPlayer.transform.right * -0.3f 
                                      + LocalPlayer.transform.up * 0.8f;

            // 3. Check proximity
            bool isNearShoulder = Vector3.Distance(rightHandPos, leftShoulderPos) < 0.25f;

            // 4. Handle press edge
            if (rightGripPress && !isNearShoulder)
            {
                // Grip pressed but not near shoulder → reset state
                _wasRightGripRightPressed = false;
            }
            else if (rightGripPress && isNearShoulder && !_wasRightGripRightPressed)
            {
                // Grip pressed ON THE SHOULDER for the first time → emulate "T"
                _wasRightGripRightPressed = true;

                var keyboard = Keyboard.current;
        
                // Press "T"
                StateEvent.From(keyboard, out var pressEvent);
                keyboard.tKey.WriteValueIntoEvent(1f, pressEvent);
                InputSystem.QueueEvent(pressEvent);

                // Immediate release (tap)
                StateEvent.From(keyboard, out var releaseEvent);
                keyboard.tKey.WriteValueIntoEvent(0f, releaseEvent);
                InputSystem.QueueEvent(releaseEvent);

                MelonLogger.Msg("[VRPlayer] Walkie-talkie activated!");
            }
            else if (!rightGripPress)
            {
                // Grip released → reset state
                _wasRightGripRightPressed = false;
            }
        }
        
        public void UpdateGpsInput()
        {
            if (LocalPlayer == null) return;

            // 1. Read input
            bool leftGripPress = NativeVRInput.GetBoolean("GripLeft_Press");
            Vector3 leftHandPos = NativeVRInput.GetPose("PoseLeft").position;
            

            // 2. Calculate right shoulder position
            Vector3 playerPos = LocalPlayer.transform.position;
            Vector3 rightShoulderPos = playerPos 
                                      + LocalPlayer.transform.right * 0.3f 
                                      + LocalPlayer.transform.up * 0.8f;

            // 3. Check proximity
            bool isNearShoulder = Vector3.Distance(leftHandPos, rightShoulderPos) < 0.25f;

            // 4. Handle press edge
            if (leftGripPress && !isNearShoulder)
            {
                // Grip pressed but not near shoulder → reset state
                _wasLeftGripRightPressed = false;
            }
            else if (leftGripPress && isNearShoulder && !_wasLeftGripRightPressed)
            {
                // Grip pressed ON THE SHOULDER for the first time → emulate "M"
                _wasLeftGripRightPressed = true;

                var keyboard = Keyboard.current;
        
                // Press "M"
                StateEvent.From(keyboard, out var pressEvent);
                keyboard.mKey.WriteValueIntoEvent(1f, pressEvent);
                InputSystem.QueueEvent(pressEvent);

                // Immediate release (tap)
                StateEvent.From(keyboard, out var releaseEvent);
                keyboard.mKey.WriteValueIntoEvent(0f, releaseEvent);
                InputSystem.QueueEvent(releaseEvent);

                MelonLogger.Msg("[VRPlayer] GPS map activated!");
            }
            else if (!leftGripPress)
            {
                // Grip released → reset state
                _wasLeftGripRightPressed = false;
            }
        }
        
        public void UpdateBookInput()
        {
            if (LocalPlayer == null) return;

            // 1. Read input
            bool rightGripPress = NativeVRInput.GetBoolean("GripRight_Press");
            Vector3 rightHandPos = NativeVRInput.GetPose("PoseRight").position;
            

            // 2. Calculate right shoulder position
            Vector3 playerPos = LocalPlayer.transform.position;
            Vector3 rightShoulderPos = playerPos 
                                       + LocalPlayer.transform.right * 0.3f 
                                       + LocalPlayer.transform.up * 0.8f;

            // 3. Check proximity
            bool isNearShoulder = Vector3.Distance(rightHandPos, rightShoulderPos) < 0.25f;

            // 4. Handle press edge
            if (rightGripPress && !isNearShoulder)
            {
                // Grip pressed but not near shoulder → reset state
                _wasBGripRightPressed = false;
            }
            else if (rightGripPress && isNearShoulder && !_wasBGripRightPressed)
            {
                // Grip pressed ON THE SHOULDER for the first time → emulate "B"
                _wasBGripRightPressed = true;

                var keyboard = Keyboard.current;
        
                // Press "B"
                StateEvent.From(keyboard, out var pressEvent);
                keyboard.bKey.WriteValueIntoEvent(1f, pressEvent);
                InputSystem.QueueEvent(pressEvent);

                // Immediate release (tap)
                StateEvent.From(keyboard, out var releaseEvent);
                keyboard.bKey.WriteValueIntoEvent(0f, releaseEvent);
                InputSystem.QueueEvent(releaseEvent);

                MelonLogger.Msg("[VRPlayer] Notebook activated!");
            }
            else if (!rightGripPress)
            {
                // Grip released → reset state
                _wasBGripRightPressed = false;
            }
        }
        
        
    }
}