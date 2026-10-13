namespace GlassOnTheStreet.Web.Models.Api;

/// <summary>Checks a submission's values beyond what the form binder enforces.</summary>
public static class ReportSubmissionRules
{
    /// <summary>The first year of MPD's current feed; an earlier date can't be a real report here.</summary>
    public static readonly DateOnly Earliest = new(2019, 1, 1);

    /// <summary>Returns what is wrong with the submission, or null when it is acceptable.</summary>
    public static string? Validate(ReportSubmission submission, DateOnly todayCentral)
    {
        if (submission.ReportedDate > todayCentral || submission.ReportedDate < Earliest)
        {
            return "The date must be between January 1, 2019 and today.";
        }

        // The form offers only these two; the other types belong to imported MPD records.
        if (submission.IncidentType is not (IncidentType.WindowSmashed or IncidentType.Rifled))
        {
            return "Choose whether the window was smashed or the car was rifled through.";
        }

        if (submission.TimeOfDay is { } time && !Enum.IsDefined(time))
        {
            return "That time of day is not recognized.";
        }

        return null;
    }
}
