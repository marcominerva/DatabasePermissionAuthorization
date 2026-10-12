namespace ApplicationScopeSample.Data.Entities;

public class FeatureScope
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public IList<string> Scopes { get; set; } = [];
}
