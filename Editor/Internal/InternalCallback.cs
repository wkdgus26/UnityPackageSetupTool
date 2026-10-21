using System;
using System.Reflection;

namespace PackageSetupTool
{
    // 내부 타입을 매개변수로 받는 델리게이트(Action<내부타입>, Action<내부타입, 내부타입>)를
    // object 매개변수 메서드에 바인딩한다. 매개변수 반공변 바인딩이라 내부 타입 이름을 몰라도 된다.
    public class InternalCallback
    {
        private const BindingFlags InvokeMethodFlags = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly Action<object> _onInvoke;
        private readonly Action<object, object> _onInvokePair;

        public InternalCallback(Action<object> onInvoke)
        {
            _onInvoke = onInvoke;
        }

        public InternalCallback(Action<object, object> onInvokePair)
        {
            _onInvokePair = onInvokePair;
        }

        public Delegate CreateDelegate(Type delegateType)
        {
            string methodName = _onInvoke != null ? nameof(Invoke) : nameof(InvokePair);
            return Delegate.CreateDelegate(delegateType, this, GetType().GetMethod(methodName, InvokeMethodFlags));
        }

        private void Invoke(object argument)
        {
            _onInvoke(argument);
        }

        private void InvokePair(object first, object second)
        {
            _onInvokePair(first, second);
        }
    }
}
