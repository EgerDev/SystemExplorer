namespace SystemExplorer.Core;

public static class RelationshipCapturePolicy
{
    public static bool SectionConsumesRelationships(string section) =>
        section is "Overview" or "Network" or "Services" or "Startup" or "Insights" or "Hidden";
}
