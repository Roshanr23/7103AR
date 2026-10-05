using UnityEngine;

namespace AR7103.UI
{
    /// <summary>Runs first every frame to take the previous frame's trick offset off (see AnimalTricks).</summary>
    [DefaultExecutionOrder(-1000)]
    public class TrickUndo : MonoBehaviour
    {
        [HideInInspector] public AnimalTricks tricks;
        void Update() { if (tricks != null) tricks.Undo(); }
    }
}
