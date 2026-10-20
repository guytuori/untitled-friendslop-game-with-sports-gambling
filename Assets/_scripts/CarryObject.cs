using UnityEngine;
namespace Blocks.Gameplay.Core
{
    public class CarryObject : ModularInteractable
    {
        [SerializeField]private Collider collider;
        [SerializeField]private Rigidbody rb;
        [SerializeField]private float dropForce;

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

            InteractionAddon addon = interactor.GetComponent<InteractionAddon>();
            if (addon != null)
            {
                addon.PickUpObject(this);
                return;
            }
        }

        public void PickedUp(Transform _carrySpot)
        {
            if (Collider() != null) { Collider().enabled = false; }
            if (RB() != null)
            {
                RB().isKinematic = true;
            }
            if (_carrySpot != null)
            {
                transform.parent = _carrySpot;
                transform.localPosition = Vector3.zero;
                transform.rotation = _carrySpot.rotation;

            }
        }
        public void Dropped(Transform _carrySpot)
        {
            EnterCooldown();
            if (_carrySpot != null)
            {
                transform.parent = null;
                transform.position += (Vector3.up - _carrySpot.forward) * 0.25f;
            }
            if (Collider() != null) { Collider().enabled = true; }
            if (RB() != null)
            {
                RB().isKinematic = false;
                RB().linearVelocity = GetRandomDiretion(_carrySpot) * dropForce;
                RB().angularVelocity = (transform.right * Mathf.Sign(Random.Range(-1.0f,1.0f) * dropForce)) + (Vector3.up * Random.Range(1.0f, 2.5f)) * dropForce;
            }
        }

        public Vector3 GetRandomDiretion(Transform _transform)
        {
            Vector3 vec = transform.right * Mathf.Sign(Random.Range(-1.0f, 1.0f) );
            vec += transform.forward * Mathf.Sign(Random.Range(-1.0f, 1.0f));
            vec += (Vector3.up * Random.Range(0.2f, 1.2f));



            return vec;
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