using System;
using System.Collections.Generic;
using System.Text;

namespace TakOne.SharedKernel.Common;

/// <summary>
/// Exception thrown when a requested entity is not found.
/// Caught by ErrorHandlingBehavior and converted to Result.Failure.
/// </summary>
public class NotFoundException : Exception
{
    public NotFoundException(string entityName, object key)
        : base($"Entity '{entityName}' with key '{key}' was not found.")
    {
    }
}
