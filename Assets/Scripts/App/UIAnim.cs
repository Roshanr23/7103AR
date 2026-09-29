using System;
using System.Collections;
using UnityEngine;

namespace AR7103.App
{
    /// <summary>Tiny coroutine tweens, so the UI needs no tweening package.</summary>
    public static class UIAnim
    {
        public static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);
        public static float EaseInOutCubic(float t) =>
            t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

        /// <summary>Unscaled, so fades keep running across scene loads and pauses.</summary>
        public static IEnumerator Tween(float duration, Action<float> step,
                                        Func<float, float> ease = null)
        {
            ease ??= EaseOutCubic;
            if (duration <= 0f) { step(1f); yield break; }
            for (float t = 0f; t < duration; t += Time.unscaledDeltaTime)
            {
                step(ease(Mathf.Clamp01(t / duration)));
                yield return null;
            }
            step(1f);
        }

        public static IEnumerator Fade(CanvasGroup g, float to, float duration)
        {
            if (g == null) yield break;
            float from = g.alpha;
            yield return Tween(duration, t => g.alpha = Mathf.Lerp(from, to, t), EaseInOutCubic);
        }
    }
}
