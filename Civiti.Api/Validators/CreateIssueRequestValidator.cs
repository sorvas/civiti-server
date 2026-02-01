using Civiti.Api.Infrastructure.Constants;
using Civiti.Api.Models.Requests.Issues;
using FluentValidation;

namespace Civiti.Api.Validators;

public class CreateIssueRequestValidator : AbstractValidator<CreateIssueRequest>
{
    public CreateIssueRequestValidator()
    {
        RuleFor(x => x.PhotoUrls)
            .Must(urls => urls is null || urls.Count <= IssueValidationLimits.MaxPhotoCount)
            .WithMessage($"A maximum of {IssueValidationLimits.MaxPhotoCount} photos are allowed.");
    }
}
