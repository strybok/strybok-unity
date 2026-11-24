using UnityEngine;

namespace Strybok
{
    /// <summary>
    /// Drop-in initializer: put it on a GameObject in your first scene and it calls
    /// <see cref="StrybokSdk.Initialize(StrybokOptions)"/> from the
    /// <see cref="StrybokSettings"/> asset. Subscribe to
    /// <see cref="StrybokSdk.LinkReceived"/> anywhere in your code to receive links.
    /// </summary>
    [AddComponentMenu("Strybok/Strybok Behaviour")]
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class StrybokBehaviour : MonoBehaviour
    {
        private static StrybokBehaviour s_instance;

        [Tooltip("Leave empty to load Resources/StrybokSettings.")]
        [SerializeField] private StrybokSettings settings;

        [Tooltip("Keep this object (and link handling) alive across scene loads.")]
        [SerializeField] private bool dontDestroyOnLoad = true;

        private bool _initializedHere;

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(this); // A reloaded first scene brought a second copy.
                return;
            }
            s_instance = this;
            if (dontDestroyOnLoad && transform.parent == null) DontDestroyOnLoad(gameObject);

            StrybokSettings active = settings != null ? settings : StrybokSettings.Load();
            if (active == null)
            {
                Debug.LogError(
                    "[Strybok] No settings: assign a StrybokSettings asset or create one with " +
                    "Tools > Strybok > Settings.", this);
                return;
            }
            if (StrybokSdk.IsInitialized) return;
            try
            {
                StrybokSdk.Initialize(active.ToOptions());
                _initializedHere = true;
            }
            catch (System.ArgumentException error)
            {
                Debug.LogError("[Strybok] Invalid settings: " + error.Message, active);
            }
        }

        private void OnDestroy()
        {
            if (s_instance != this) return;
            s_instance = null;
            if (_initializedHere) StrybokSdk.Shutdown();
        }
    }
}
