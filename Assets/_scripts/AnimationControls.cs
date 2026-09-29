using Unity.Collections;
using UnityEngine;

public class AnimationControls : MonoBehaviour
{

    [SerializeField] public Animator anim;


    [ReadOnly] public string ANIMATION_slide = "slide";

    public void EnterAbility(string _abilityName)
    {
        
    }

    public void PlayAnimation(string _animation)
    {
        if (Anim() == null) { return; }

        if (ContainsAnimation(Anim(), _animation))
        { Anim().Play(_animation); }
    }

    #region animation parameters



    public void SetAnimationTrigger(string _animParameter)
    {
        if (Anim() == null) { return; }

        if (ContainsParam(Anim(), _animParameter))
        { Anim().SetTrigger(_animParameter); }
    }

    public void SetAnimationParameter(string _animParameter, int _animState)
    {
        if (Anim() == null) { return; }

        if (ContainsParam(Anim(), _animParameter))
        { Anim().SetInteger(_animParameter, _animState); }
    }

    public void SetAnimationParameter(string _animParameter, float _animState)
    {
        if (Anim() == null) { return; }

        if (ContainsParam(Anim(), _animParameter))
        { Anim().SetFloat(_animParameter, _animState); }
    }

    public void SetAnimationParameter(string _animParameter, bool _animState)
    {
        if (Anim() == null) { return; }

        if (ContainsParam(Anim(), _animParameter))
        { Anim().SetBool(_animParameter, _animState); }
    }

    public bool ContainsParam(Animator _Anim, string _ParamName)
    {
        foreach (AnimatorControllerParameter param in _Anim.parameters)
        {
            if (param.name.ToLower() == _ParamName.ToLower()) return true;
        }
        return false;
    }
    public bool ContainsAnimation(Animator _Anim, string _name)
    {

        return _Anim.HasState(0, Animator.StringToHash(_name));
    }
    # endregion 
    public Animator Anim()
    {
        if (anim == null)
        { anim = GetComponent<Animator>(); }

        return anim;
    }

}
