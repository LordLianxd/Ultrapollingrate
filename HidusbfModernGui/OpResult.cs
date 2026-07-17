namespace HidusbfModernGui
{
    // Result of an operation that touches the system. Carries the reason on failure
    // so the UI can tell the user what actually went wrong instead of a generic box.
    public readonly record struct OpResult(bool Success, string? Error)
    {
        public static OpResult Ok() => new(true, null);
        public static OpResult Fail(string error) => new(false, error);
    }
}
