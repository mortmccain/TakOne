using System;
using System.Collections.Generic;
using System.Text;

namespace TakOne.SharedKernel.Common;

/// <summary>
/// Exception thrown when a business rule is violated.
/// Caught by ErrorHandlingBehavior and converted to Result.Failure.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception innerException)
        : base(message, innerException) { }
}
