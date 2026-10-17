using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A ScriptableObject that acts as a parameter-less global event channel. This asset allows for a decoupled
    /// event system where different parts of the application can communicate without holding direct references
    /// to each other. "Listeners" register with the event, and "Raisers" can invoke the event, triggering all
    /// registered listener methods. This is the base class for events that do not carry a payload.
    /// </summary>
    [CreateAssetMenu(fileName = "GameEvent", menuName = "Game Events/Game Event")]
    public class GameEvent : ScriptableObject
    {
        #region Private Fields

        /// <summary>
        /// A list of all the methods (actions) that have subscribed to this event.
        /// Marked as `readonly` because the list itself is never replaced, only its contents are modified.
        /// </summary>
        private readonly List<System.Action> m_Listeners = new List<System.Action>();

        #endregion

        #region Public Methods

        /// <summary>
        /// Invokes all registered listener methods.
        /// This method iterates backwards through the list of listeners. This is a robust way to handle cases
        /// where a listener might unregister itself from the event as part of its invoked logic,
        /// preventing collection modification errors during the loop.
        /// </summary>
        public void Raise()
        {
            // These assets outlive scenes and sessions, so a listener whose object was destroyed without
            // unregistering is dropped here rather than called. And one listener throwing doesn't stop the rest.
            for (int i = m_Listeners.Count - 1; i >= 0; i--)
            {
                if (i >= m_Listeners.Count) continue; // a listener unregistered others
                System.Action listener = m_Listeners[i];
                if (listener.Target is Object owner && owner == null)
                {
                    m_Listeners.RemoveAt(i);
                    continue;
                }
                try
                {
                    listener.Invoke();
                }
                catch (System.Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        /// <summary>
        /// Subscribes a listener (a parameter-less method) to this event.
        /// The listener will be invoked whenever the <see cref="Raise"/> method is called.
        /// </summary>
        /// <param name="listener">The method to be called when the event is raised.</param>
        public void RegisterListener(System.Action listener)
        {
            // Add the listener only if it's not already in the list to prevent duplicate registrations.
            if (!m_Listeners.Contains(listener))
            {
                m_Listeners.Add(listener);
            }
        }

        /// <summary>
        /// Unsubscribes a listener from this event.
        /// The listener will no longer be invoked when the event is raised.
        /// </summary>
        /// <param name="listener">The method to be removed from the event's subscribers.</param>
        public void UnregisterListener(System.Action listener)
        {
            // Remove the listener only if it exists in the list to avoid errors.
            if (m_Listeners.Contains(listener))
            {
                m_Listeners.Remove(listener);
            }
        }

        #endregion
    }
}
