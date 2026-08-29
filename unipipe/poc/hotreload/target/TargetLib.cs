using System.Runtime.CompilerServices;

// Lives in its own assembly so that "private" actually means something to the
// compiler and the runtime. A patch compiled elsewhere cannot legally touch
// _hidden or Secret() — which is exactly the barrier Stage B is about.
public class TargetLib
{
    private int _hidden = 7;
    public int Public = 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Secret(int x) => _hidden * 10 + x;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int CallSecret(int x) => Secret(x);
}
