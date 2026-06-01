using System.Reflection.Metadata;

// Minimal ISignatureTypeProvider so MethodDefinition.DecodeSignature can run as a
// validity probe (the returned strings are irrelevant — we only care if it throws).
sealed class DummySig : ISignatureTypeProvider<string, object>
{
    public string GetArrayType(string e, ArrayShape s) => e + "[]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g;
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rk) => "td";
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rk) => "tr";
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte rk) => "ts";
}
