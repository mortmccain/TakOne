namespace TakOne.Application.Common.Interfaces;

public interface ICurrentUserService
{
    Guid UserId { get; }
    string PersonalId { get; }
    string FullName { get; }
    bool IsAuthenticated { get; }       // since the user can be not authenticated before logging,
                                        // shouldn't the ID property be nullable?
}