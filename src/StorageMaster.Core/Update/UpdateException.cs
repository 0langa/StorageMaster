using StorageMaster.Core.Models;

namespace StorageMaster.Core.Update;

public sealed class UpdateException : Exception
{
    public UpdateFailureKind Kind { get; }

    public UpdateException(UpdateFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
