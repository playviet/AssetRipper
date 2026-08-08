using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A struct whose every field is a float, which aapcs64 passes and returns in one vector register per field.
/// Unity's geometry types are nothing else, so this is most of what a game passes around.
/// </summary>
public static class HomogeneousFloatStruct
{
    /// <summary>
    /// How many floats the type is, if that is all it is - up to the four the rule allows. Null for anything
    /// else, including a plain float, which is not a struct and needs none of this.
    /// </summary>
    public static int? Count(TypeAnalysisContext type, int depth = 0)
    {
        if (depth > 3 || !type.IsValueType)
            return null;

        if (type.FullName == "System.Single")
            return 1;

        //An enum has an underlying integer field, and a primitive that is not a float has itself.
        if (type.Namespace == nameof(System) || type.Fields.Count == 0)
            return null;

        var total = 0;

        foreach (var field in type.Fields)
        {
            if (field.IsStatic)
                continue;

            if (Count(field.FieldType, depth + 1) is not { } floats)
                return null;

            total += floats;

            if (total > 4)
                return null;
        }

        return total > 0 ? total : null;
    }

    /// <summary>
    /// The type's fields, where every one of them is a float and nothing else - one per register it travels
    /// in, in the order aapcs64 assigns them.
    /// </summary>
    public static List<FieldAnalysisContext>? Fields(TypeAnalysisContext type)
    {
        var fields = new List<FieldAnalysisContext>();

        foreach (var field in type.Fields)
        {
            if (field.IsStatic)
                continue;

            if (field.FieldType.FullName != "System.Single")
                return null;

            fields.Add(field);
        }

        return fields.Count > 0 ? fields : null;
    }

    /// <summary>
    /// Whether a value of this type takes more than one register, so that a register holding part of it says
    /// nothing about the whole - which is why the type must not be inferred from where such a value is passed.
    /// </summary>
    public static bool SpansSeveralRegisters(TypeAnalysisContext type) => Count(type) is > 1;
}
