using System;
using System.Collections.Generic;
using System.Text;

namespace TakOne.SharedKernel.Common;

/// <summary>
/// Exception thrown when a business rule is violated. Caught by individual
/// command handlers in their <c>try/catch</c> blocks and converted to
/// <c>Result.Failure</c>. Wolverine 6.x's <c>FinallyAsync</c> convention
/// cannot return a value to replace the handler's output, so exception-to-
/// Result conversion must happen inside the handler, not in middleware.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception innerException)
        : base(message, innerException) { }
}
