namespace SnapBoard.Update.Velopack;

internal sealed class UpdateSignatureException : Exception
{
    public UpdateSignatureException(string message)
        : base(message)
    {
    }

    public UpdateSignatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class UpdateSourceConflictException : Exception
{
    public UpdateSourceConflictException(string message)
        : base(message)
    {
    }
}

internal sealed class UpdateSourcesUnavailableException : Exception
{
    public UpdateSourcesUnavailableException(Exception? innerException)
        : base("No trusted update source is available.", innerException)
    {
    }
}

internal sealed class OfficialUpdateSourceUnavailableException : Exception
{
}
