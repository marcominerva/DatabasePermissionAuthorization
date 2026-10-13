namespace ApplicationScopeSample.Data.Entities;

public class FeaturePermission
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public IList<string> Permissions { get; set; } = [];
}
