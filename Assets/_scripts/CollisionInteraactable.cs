using UnityEngine;
namespace Blocks.Gameplay.Core
{
    public class CollisionInteraactable : ModularInteractable
    {
        [SerializeField] private Collider collider;
        [SerializeField] private Rigidbody rb;
        [SerializeField] private float collideForce;

        public override void Interact(GameObject interactor)
        {
            if (interactor == null)
            {
                Debug.LogWarning($"[ModularInteractable] {gameObject.name}: Interact called with null interactor.", this);
                return;
            }


            if (!CanInteract())
            {
                return;
            }

            if (RB()) 
            {
                RB().AddForce((transform.position - interactor.transform.position).normalized * collideForce,ForceMode.Impulse);
            }

            if (interactor.GetComponent<CoreMovement>())
            {
                interactor.GetComponent<CoreMovement>().ApplyExternalForce((Vector3.up + (interactor.transform.position- transform.position)).normalized * collideForce,ForceMode.Impulse);
            }

        }


        public Collider Collider()
        {
            if (collider == null)
            { collider = GetComponent<Collider>(); }
            return collider;
        }
        public Rigidbody RB()
        {
            if (rb == null)
            { rb = GetComponent<Rigidbody>(); }
            return rb;
        }

    }

}