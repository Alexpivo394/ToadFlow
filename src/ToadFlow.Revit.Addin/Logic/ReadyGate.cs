using System.Threading;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>
///     «Revit готов» уходит агенту один раз: после прогрева или по таймауту прогрева — что наступит раньше. Потокобезопасно:
///     прогрев идёт в потоке Revit, таймаут — в потоке таймера.
/// </summary>
public sealed class ReadyGate
{
    private int _open;

    /// <returns>true — вызвавший первый и должен сообщить о готовности.</returns>
    public bool TryOpen() => Interlocked.Exchange(ref _open, 1) == 0;
}
