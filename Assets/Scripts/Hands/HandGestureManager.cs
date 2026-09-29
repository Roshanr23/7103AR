using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Vuforia;

namespace AR7103.Hands
{
    [Serializable] public class GestureEvent : UnityEvent<Gesture> { }

    /// <summary>
    /// Drives hand-gesture detection off the Vuforia camera feed.
    ///
    /// Put this on any active GameObject in the scene (the ARCamera is a fine
    /// home). Hook the UnityEvents in the Inspector to decide what a gesture
    /// actually does -- nothing is wired to the animals yet on purpose.
    ///
    /// Only functional on a real iOS device: Apple Vision needs a camera, and
    /// Vuforia does not deliver frames in the Editor either. In the Editor this
    /// runs harmlessly and reports zero hands.
    /// </summary>
    public class HandGestureManager : MonoBehaviour
    {
        [Header("Detection")]
        [Tooltip("Hand detection runs at this rate, not once per rendered frame. " +
                 "Vision costs a few ms per call and hands do not move that fast; " +
                 "12-15 Hz looks identical to 60 and saves a lot of battery.")]
        [Range(2f, 30f)] public float detectionsPerSecond = 12f;

        [Tooltip("How many hands to look for. Each one costs detection time.")]
        [Range(1, 2)] public int maxHands = 1;

        [Tooltip("Per-joint confidence below which a joint is treated as missing.")]
        [Range(0f, 1f)] public float minJointConfidence = 0.4f;

        [Tooltip("A hand needs at least this many confident joints to be classified.")]
        [Range(4, 21)] public int minConfidentJoints = 8;

        [Header("Stability")]
        [Tooltip("A gesture must hold for this many consecutive detections before it " +
                 "fires. Raw per-frame classification flickers between poses on the " +
                 "way in and out of a gesture; without this you get spurious events.")]
        [Range(1, 10)] public int framesToConfirm = 3;

        [Tooltip("Seconds before the same gesture can fire again.")]
        [Range(0f, 3f)] public float retriggerCooldown = 0.75f;

        [Header("Events")]
        public GestureEvent onGestureBegan;     // fires once when a gesture is confirmed
        public GestureEvent onGestureEnded;     // fires when that gesture stops
        public UnityEvent onHandLost;

        [Header("Debug")]
        public bool logDetections = false;

        // Vuforia hands us frames in camera-buffer orientation, not screen
        // orientation. 6 = CGImagePropertyOrientation.right, which is correct for
        // a portrait-held iPhone rear camera. If gestures behave as though the
        // hand is rotated, this is the first value to change (1/3/6/8).
        [Header("Advanced")]
        [Tooltip("CGImagePropertyOrientation: 1=up, 3=down, 6=right, 8=left")]
        public int cgOrientation = 6;

        public Gesture CurrentGesture { get; private set; } = Gesture.None;
        public bool HandVisible { get; private set; }
        public Vector2 PalmCenterNormalised { get; private set; }

        HandObservation[] _results;
        float _nextDetectTime;
        Gesture _candidate = Gesture.None;
        int _candidateStreak;
        readonly Dictionary<Gesture, float> _lastFired = new Dictionary<Gesture, float>();
        bool _formatRequested;
        bool _warnedUnsupported;

        void Awake()
        {
            _results = HandPoseProvider.AllocResults(Mathf.Max(1, maxHands));
        }

        void OnEnable()
        {
            VuforiaApplication.Instance.OnVuforiaStarted += OnVuforiaStarted;
            if (VuforiaApplication.Instance.IsRunning) OnVuforiaStarted();
        }

        void OnDisable()
        {
            VuforiaApplication.Instance.OnVuforiaStarted -= OnVuforiaStarted;
        }

        void OnVuforiaStarted()
        {
            // Vuforia only produces CPU-accessible frames for formats that have
            // been explicitly requested, and the request must happen after the
            // engine starts. Grayscale is the cheapest format Vision accepts and
            // hand pose does not benefit from colour.
            if (_formatRequested) return;
            bool ok = VuforiaBehaviour.Instance.CameraDevice.SetFrameFormat(PixelFormat.GRAYSCALE, true);
            _formatRequested = true;
            if (!ok)
                Debug.LogWarning("[HandGesture] Could not register GRAYSCALE frame format; " +
                                 "no camera frames will reach hand detection.", this);
        }

        void Update()
        {
            if (Time.unscaledTime < _nextDetectTime) return;
            _nextDetectTime = Time.unscaledTime + (1f / Mathf.Max(1f, detectionsPerSecond));

            if (!HandPoseProvider.IsSupported)
            {
                if (!_warnedUnsupported)
                {
                    _warnedUnsupported = true;
                    Debug.Log("[HandGesture] Hand pose unavailable here " +
                              "(Editor, or iOS < 14). Pipeline idle.", this);
                }
                return;
            }

            if (!VuforiaApplication.Instance.IsRunning) return;

            var image = VuforiaBehaviour.Instance.CameraDevice.GetCameraImage(PixelFormat.GRAYSCALE);
            if (image == null || image.Pixels == null || image.Width <= 0 || image.Height <= 0)
                return;

            int count = HandPoseProvider.Detect(image.Pixels, image.Width, image.Height,
                                                channels: 1, orientation: cgOrientation,
                                                maxHands: maxHands, results: _results);

            if (count <= 0) { HandleNoHand(count); return; }

            ref HandObservation hand = ref _results[0];
            if (!hand.IsReliable(minJointConfidence, minConfidentJoints)) { HandleNoHand(0); return; }

            HandVisible = true;
            PalmCenterNormalised = GestureRecognizer.PalmCenter(hand);
            Gesture g = GestureRecognizer.Classify(hand, minJointConfidence);

            if (logDetections)
                Debug.Log($"[HandGesture] hands={count} gesture={g} palm={PalmCenterNormalised}", this);

            Confirm(g);
        }

        void HandleNoHand(int nativeCode)
        {
            if (nativeCode < 0 && logDetections)
                Debug.LogWarning($"[HandGesture] native detect returned {nativeCode}", this);

            if (HandVisible)
            {
                HandVisible = false;
                if (CurrentGesture != Gesture.None) onGestureEnded?.Invoke(CurrentGesture);
                CurrentGesture = Gesture.None;
                _candidate = Gesture.None;
                _candidateStreak = 0;
                onHandLost?.Invoke();
            }
        }

        void Confirm(Gesture g)
        {
            if (g == _candidate) _candidateStreak++;
            else { _candidate = g; _candidateStreak = 1; }

            if (_candidateStreak < framesToConfirm) return;
            if (g == CurrentGesture) return;

            if (CurrentGesture != Gesture.None) onGestureEnded?.Invoke(CurrentGesture);
            CurrentGesture = g;
            if (g == Gesture.None) return;

            float now = Time.unscaledTime;
            if (_lastFired.TryGetValue(g, out float last) && now - last < retriggerCooldown) return;
            _lastFired[g] = now;

            onGestureBegan?.Invoke(g);
        }
    }
}
