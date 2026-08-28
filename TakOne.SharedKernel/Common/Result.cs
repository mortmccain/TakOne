using System;
using System.Collections.Generic;
using System.Text;

namespace TakOne.SharedKernel.Common;

/// <summary>
/// Represents the outcome of an operation. Success or Failure.
/// Used as the return type for all Commands and Queries.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }

    protected Result(bool isSuccess, string error)
    {
        if (isSuccess && !string.IsNullOrEmpty(error))
            throw new InvalidOperationException("A successful result cannot have an error.");
        if (!isSuccess && string.IsNullOrEmpty(error))
            throw new InvalidOperationException("A failed result must have an error.");

        IsSuccess = isSuccess;
        Error = error;
    }
    // something is wrong, here's why
    public static Result Success() => new(true, string.Empty);
    public static Result Failure(string error) => new(false, error);

    // something is wrong, here's why + some data
    // might want to delete the shortcut functions
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);
    // calls the method on the child class. this is a code smell but it is done for a nicer looking API
    public static Result<T> Failure<T>(string error) => Result<T>.Failure(error);
}

/// <summary>
/// Generic Result with a value on success.
///
/// VALUE ACCESS SAFETY (Brutal Code Review v3 finding #16):
///   The <see cref="Value"/> getter throws
///   <see cref="InvalidOperationException"/> if the result is a failure.
///   Previously, <see cref="Failure"/> stored <c>default!</c> and the
///   getter returned it silently — so a failed <c>Result&lt;int&gt;</c>
///   had <c>Value = 0</c> and a failed <c>Result&lt;string&gt;</c> had
///   <c>Value = null</c>. Callers that forgot to check
///   <see cref="Result.IsSuccess"/> silently got 0/null and propagated
///   the bug downstream. The throw makes the footgun LOUD: you must
///   check <c>IsSuccess</c> before accessing <c>Value</c>.
/// </summary>
public class Result<T> : Result
{
    // Backing field for Value. Stored separately so the getter can
    // enforce the IsSuccess invariant before returning.
    private readonly T _value;

    /// <summary>
    /// The success value. Throws <see cref="InvalidOperationException"/>
    /// if accessed on a failed result — always check
    /// <see cref="Result.IsSuccess"/> first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Result.IsSuccess"/> is false. The message
    /// includes the <see cref="Result.Error"/> for diagnostics.
    /// </exception>
    public T Value
    {
        get
        {
            if (!IsSuccess)
            {
                throw new InvalidOperationException(
                    "Cannot access Value of a failed Result. Check IsSuccess before accessing Value. " +
                    $"Error: {Error}");
            }

            return _value;
        }
    }

    protected internal Result(T value, bool isSuccess, string error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public static Result<T> Success(T value) => new(value, true, string.Empty);

    // Creates a failed Result. The Value is NOT accessible (the getter
    // throws) — callers must check IsSuccess and read Error instead.
    public static new Result<T> Failure(string error) => new(default!, false, error);
}
