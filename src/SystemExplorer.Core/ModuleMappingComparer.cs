namespace SystemExplorer.Core;

public sealed class ModuleMappingComparer : IEqualityComparer<ModuleObservation>
{
    public static ModuleMappingComparer Instance { get; } = new();

    public bool Equals(ModuleObservation? x, ModuleObservation? y) =>
        ReferenceEquals(x, y) || x is not null && y is not null &&
        x.BaseAddress == y.BaseAddress && x.SizeBytes == y.SizeBytes &&
        StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

    public int GetHashCode(ModuleObservation obj) =>
        HashCode.Combine(obj.BaseAddress, obj.SizeBytes, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
}
