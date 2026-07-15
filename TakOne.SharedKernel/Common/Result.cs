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
/// </summary>
public class Result<T> : Result
{
    public T Value { get; }

    protected internal Result(T value, bool isSuccess, string error)
        : base(isSuccess, error)
    {
        Value = value;
    }

    // might want to delete the shortcut functions
    public static Result<T> Success(T value) => new(value, true, string.Empty);
    public static new Result<T> Failure(string error) => new(default!, false, error);
    //            ^^^
    //            This hides the parent's Failure method
}
