namespace FlowerShop.Shared;

/// <summary>Railway-style result type: no exceptions for expected failures.</summary>
public readonly record struct Result<T>(T? Value, string? Error)
{
    public bool IsOk => Error is null;
    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(string error) => new(default, error);

    public Result<U> Map<U>(Func<T, U> f) => IsOk ? Result<U>.Ok(f(Value!)) : Result<U>.Fail(Error!);
    public Result<U> Bind<U>(Func<T, Result<U>> f) => IsOk ? f(Value!) : Result<U>.Fail(Error!);
    public T Match(Func<string, T> onError) => IsOk ? Value! : onError(Error!);
}
