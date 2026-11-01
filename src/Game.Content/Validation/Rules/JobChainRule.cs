namespace Game.Content.Validation.Rules;

/// <summary>The <c>parent_job_id</c> chain of every job ends at a root job (no loops).</summary>
public sealed class JobChainRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "job-chain";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var jobs = context.Database.Jobs;
        foreach (var job in jobs.All)
        {
            var chain = new List<string> { job.Id };
            var current = job;
            while (!string.IsNullOrEmpty(current.ParentJobId) && jobs.TryGet(current.ParentJobId!, out var parent))
            {
                if (parent.Id == job.Id)
                {
                    context.Error(job.Id, "parent_job_id", ValidationCodes.JobCycle,
                        $"Job parent chain loops: {string.Join(" -> ", chain)} -> {job.Id}");
                    break;
                }
                if (chain.Contains(parent.Id)) break; // loops elsewhere; reported on that cycle's members
                chain.Add(parent.Id);
                current = parent;
            }
        }
    }
}
