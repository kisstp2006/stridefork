// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Core.Yaml.Serialization;

namespace Stride.Core.Reflection;

/// <summary>
/// Default implementation of a <see cref="ITypeDescriptor"/>.
/// </summary>
public class ObjectDescriptor : ITypeDescriptor
{
    protected static readonly string SystemCollectionsNamespace = typeof(int).Namespace!;
    public static readonly ShouldSerializePredicate ShouldSerializeDefault = (_, __) => true;
    private static readonly List<IMemberDescriptor> EmptyMembers = [];

    private readonly ITypeDescriptorFactory factory;
    private IMemberDescriptor[] members;
    private Dictionary<string, IMemberDescriptor> mapMembers;
    private HashSet<string> remapMembers;
    private static readonly object[] EmptyObjectArray = [];
    private readonly bool emitDefaultValues;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectDescriptor" /> class.
    /// </summary>
    public ObjectDescriptor(ITypeDescriptorFactory factory, Type type, bool emitDefaultValues, IMemberNamingConvention namingConvention)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(namingConvention);

        this.factory = factory;
        Type = type;
        IsCompilerGenerated = AttributeRegistry.GetAttribute<CompilerGeneratedAttribute>(type) != null;
        this.emitDefaultValues = emitDefaultValues;
        NamingConvention = namingConvention;

        Attributes = AttributeRegistry.GetAttributes(type);

        Style = DataStyle.Any;
        foreach (var attribute in Attributes)
        {
            if (attribute is DataStyleAttribute styleAttribute)
            {
                Style = styleAttribute.Style;
            }
        }

        // Get DefaultMemberMode from DataContract
        DefaultMemberMode = DataMemberMode.Default;
        var currentType = type;
        while (currentType != null)
        {
            var dataContractAttribute = AttributeRegistry.GetAttribute<DataContractAttribute>(currentType);
            if (dataContractAttribute != null && (dataContractAttribute.Inherited || currentType == type))
            {
                DefaultMemberMode = dataContractAttribute.DefaultMemberMode;
                break;
            }
            currentType = currentType.BaseType;
        }
    }

    protected IAttributeRegistry AttributeRegistry => factory.AttributeRegistry;

    public Type Type { get; }

    public IEnumerable<IMemberDescriptor> Members => members;

    public int Count => members?.Length ?? 0;

    public bool HasMembers => members?.Length > 0;

    public virtual DescriptorCategory Category => DescriptorCategory.Object;

    /// <summary>
    /// Gets the naming convention.
    /// </summary>
    /// <value>The naming convention.</value>
    public IMemberNamingConvention NamingConvention { get; }

    /// <summary>
    /// Gets attributes attached to this type.
    /// </summary>
    public List<Attribute> Attributes { get; }

    public DataStyle Style { get; }

    public DataMemberMode DefaultMemberMode { get; }

    public bool IsCompilerGenerated { get; }

    public bool IsMemberRemapped(string name)
    {
        return remapMembers?.Contains(name) == true;
    }

    public IMemberDescriptor this[string name]
    {
        get
        {
            if (mapMembers == null)
                throw new KeyNotFoundException();
            return mapMembers[name];
        }
    }

    public IMemberDescriptor? TryGetMember(string name)
    {
        if (mapMembers == null)
            return null;
        mapMembers.TryGetValue(name, out var member);
        return member;
    }

    public override string ToString()
    {
        return Type.ToString();
    }

    public virtual void Initialize(IComparer<object> keyComparer)
    {
        if (members != null)
            return;

        var memberList = PrepareMembers();

        // Sort members by name
        // This is to make sure that properties/fields for an object
        // are always displayed in the same order
        if (keyComparer != null)
        {
            memberList.Sort(keyComparer);
        }

        // Free the member list
        members = [.. memberList];

        // If no members found, we don't need to build a dictionary map
        if (members.Length == 0)
            return;

        mapMembers = new Dictionary<string, IMemberDescriptor>(members.Length);

        foreach (var member in members)
        {
            if (mapMembers.TryGetValue(member.Name, out var existingMember))
            {
                throw new InvalidOperationException("Failed to get ObjectDescriptor for type [{0}]. The member [{1}] cannot be registered as a member with the same name is already registered [{2}]".ToFormat(Type.FullName, member, existingMember));
            }

            mapMembers.Add(member.Name, member);

            // If there is any alternative names, register them
            if (member.AlternativeNames != null)
            {
                foreach (var alternateName in member.AlternativeNames)
                {
                    if (mapMembers.TryGetValue(alternateName, out existingMember))
                    {
                        throw new InvalidOperationException($"Failed to get ObjectDescriptor for type [{Type.FullName}]. The member [{member}] cannot be registered as a member with the same name [{alternateName}] is already registered [{existingMember}]");
                    }
                    remapMembers ??= [];

                    mapMembers[alternateName] = member;
                    remapMembers.Add(alternateName);
                }
            }
        }
    }

    public bool Contains(string memberName)
    {
        return mapMembers?.ContainsKey(memberName) == true;
    }

    protected virtual List<IMemberDescriptor> PrepareMembers()
    {
        if (Type == typeof(Type))
        {
            return EmptyMembers;
        }

        var bindingFlags = BindingFlags.Instance | BindingFlags.Public;

        var metadataTypeAttributes = Type.GetCustomAttributes<DataContractMetadataTypeAttribute>(inherit: true);
        var metadataClassMemberInfos = metadataTypeAttributes.Any() ? new List<(MemberInfo MemberInfo, Type MemberType)>() : null;
        foreach (var metadataTypeAttr in metadataTypeAttributes)
        {
            var metadataType = metadataTypeAttr.MetadataClassType;
            metadataClassMemberInfos!.AddRange(from propertyInfo in metadataType.GetProperties(bindingFlags)
                                               where propertyInfo.CanRead && propertyInfo.GetIndexParameters().Length == 0 && IsMemberToVisit(propertyInfo)
                                               select (propertyInfo as MemberInfo, propertyInfo.PropertyType));

            // Add all public fields
            metadataClassMemberInfos.AddRange(from fieldInfo in metadataType.GetFields(bindingFlags)
                                              where fieldInfo.IsPublic && IsMemberToVisit(fieldInfo)
                                              select (fieldInfo as MemberInfo, fieldInfo.FieldType));
        }

        // TODO: we might want an option to disable non-public.
        if (Category is DescriptorCategory.Object or DescriptorCategory.NotSupportedObject)
            bindingFlags |= BindingFlags.NonPublic;

        var memberList = (from propertyInfo in Type.GetProperties(bindingFlags)
                          where Type.IsAnonymous() || IsAccessibleThroughAccessModifiers(propertyInfo)
                          where propertyInfo.GetIndexParameters().Length == 0 && IsMemberToVisit(propertyInfo)
                          select new PropertyDescriptor(factory.Find(propertyInfo.PropertyType), propertyInfo, NamingConvention.Comparer)
                          into member
                          where PrepareMember(member, metadataClassMemberInfos?.FirstOrDefault(x => x.MemberInfo.Name == member.OriginalName && x.MemberType == member.Type).MemberInfo)
                          select member as IMemberDescriptor).ToList();

        // Add all public fields
        memberList.AddRange(from fieldInfo in Type.GetFields(bindingFlags)
                            where fieldInfo.IsPublic || (fieldInfo.IsAssembly && fieldInfo.GetCustomAttribute<DataMemberAttribute>() != null)
                            where IsMemberToVisit(fieldInfo)
                            select new FieldDescriptor(factory.Find(fieldInfo.FieldType), fieldInfo, NamingConvention.Comparer)
                            into member
                            where PrepareMember(member, metadataClassMemberInfos?.FirstOrDefault(x => x.MemberInfo.Name == member.OriginalName && x.MemberType == member.Type).MemberInfo)
                            select member);

        // Allows adding dynamic members per type
        (AttributeRegistry as AttributeRegistry)?.PrepareMembersCallback?.Invoke(this, memberList);

        return memberList;
    }

    static bool IsAccessibleThroughAccessModifiers(PropertyInfo property)
    {
        var get = property.GetMethod;
        var set = property.SetMethod;

        if (get == null)
            return false;

        bool forced = property.GetCustomAttribute<DataMemberAttribute>() is not null;

        if (forced && (get.IsPublic || get.IsAssembly))
            return true;

        // By default, allow access for get-only auto-property, i.e.: { get; } but not { get => }
        // as the later may create side effects, and without a setter, we can't 'set' as a fallback for those exceptional cases.
        if (get.IsPublic)
            return set?.IsPublic == true || (set == null && TryGetBackingField(property, out _));

        return false;
    }

    static bool TryGetBackingField(PropertyInfo property, [MaybeNullWhen(false)] out FieldInfo backingField)
    {
        backingField = property.DeclaringType?.GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        return backingField != null;
    }

    protected virtual bool PrepareMember(MemberDescriptorBase member, MemberInfo metadataClassMemberInfo)
    {
        var memberType = member.Type;

        // Start with DataContractAttribute.DefaultMemberMode (if set)
        member.Mode = DefaultMemberMode;
        member.Mask = 1;

        var attributes = AttributeRegistry.GetAttributes(member.MemberInfo);
        if (metadataClassMemberInfo != null)
        {
            var metadataAttributes = AttributeRegistry.GetAttributes(metadataClassMemberInfo);
            attributes.InsertRange(0, metadataAttributes);
        }

        // Gets the style
        if (attributes.FirstOrDefault(x => x is DataStyleAttribute) is DataStyleAttribute styleAttribute)
        {
            member.Style = styleAttribute.Style;
            member.ScalarStyle = styleAttribute.ScalarStyle;
        }

        // Handle member attribute
        var memberAttribute = attributes.FirstOrDefault(x => x is DataMemberAttribute) as DataMemberAttribute;
        if (memberAttribute != null)
        {
            ((IMemberDescriptor)member).Mask = memberAttribute.Mask;
            member.Mode = memberAttribute.Mode;
            if (!member.HasSet)
            {
                if (memberAttribute.Mode == DataMemberMode.Assign || memberType.IsValueType || memberType == typeof(string))
                    member.Mode = DataMemberMode.Never;
            }
            member.Order = memberAttribute.Order;
        }

        // If mode is Default, let's resolve to the actual mode depending on getter/setter existence and object type
        if (member.Mode == DataMemberMode.Default)
        {
            // The default mode is Content, which will not use the setter to restore value if the object is a class (but behave like Assign for value types)
            member.Mode = DataMemberMode.Content;
            if (!member.HasSet && (memberType == typeof(string) || !memberType.IsClass) && !memberType.IsInterface && !Type.IsAnonymous())
            {
                // If there is no setter, and the value is a string or a value type, we won't write the object at all.
                member.Mode = DataMemberMode.Never;
            }
        }

        // Process all attributes just once instead of getting them one by one
        DefaultValueAttribute? defaultValueAttribute = null;
        foreach (var attribute in attributes)
        {
            if (attribute is DefaultValueAttribute valueAttribute)
            {
                // If we've already found one, don't overwrite it
                defaultValueAttribute ??= valueAttribute;
                continue;
            }

            if (attribute is DataAliasAttribute yamlRemap)
            {
                member.AlternativeNames ??= [];

                if (!string.IsNullOrWhiteSpace(yamlRemap.Name))
                {
                    member.AlternativeNames.Add(yamlRemap.Name);
                }
            }
        }

        // If it's a private member, check it has a YamlMemberAttribute on it
        if (!member.IsPublic)
        {
            if (memberAttribute == null)
                return false;
        }

        // If this member cannot be serialized, remove it from the list
        if (member.Mode == DataMemberMode.Never)
        {
            return false;
        }

        // ShouldSerialize
        //	  YamlSerializeAttribute(Never) => false
        //	  ShouldSerializeSomeProperty => call it
        //	  DefaultValueAttribute(default) => compare to it
        //	  otherwise => true
        var shouldSerialize = Type.GetMethod("ShouldSerialize" + member.OriginalName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (shouldSerialize != null && shouldSerialize.ReturnType == typeof(bool) && member.ShouldSerialize == null)
            member.ShouldSerialize = (obj, _) => (bool)shouldSerialize.Invoke(obj, EmptyObjectArray)!;

        if (defaultValueAttribute != null && member.ShouldSerialize == null && !emitDefaultValues)
        {
            member.DefaultValueAttribute = defaultValueAttribute;
            var defaultValue = defaultValueAttribute.Value;
            var defaultType = defaultValue?.GetType();
            if (defaultType?.IsNumeric() == true && defaultType != memberType)
            {
                try
                {
                    defaultValue = Convert.ChangeType(defaultValue, memberType);
                }
                catch (InvalidCastException)
                {
                }
            }
            member.ShouldSerialize = (obj, parentTypeMemberDesc) =>
            {
                if (parentTypeMemberDesc?.HasDefaultValue ?? false)
                {
                    var parentDefaultValue = parentTypeMemberDesc.DefaultValue;
                    if (parentDefaultValue != null)
                    {
                        // The parent class holding this object type has defined it's own default value for this type
                        var parentDefaultValueMemberValue = member.Get(parentDefaultValue); // This is the real default value for this object
                        return !Equals(parentDefaultValueMemberValue, member.Get(obj));
                    }
                }
                return !Equals(defaultValue, member.Get(obj));
            };
        }

        member.ShouldSerialize ??= ShouldSerializeDefault;

        member.Name = !string.IsNullOrEmpty(memberAttribute?.Name) ? memberAttribute.Name : NamingConvention.Convert(member.OriginalName);

        return true;
    }

    protected bool IsMemberToVisit(MemberInfo memberInfo)
    {
        // Remove all SyncRoot from members
        if (memberInfo is PropertyInfo && memberInfo.Name == "SyncRoot" && memberInfo.DeclaringType != null && (memberInfo.DeclaringType.Namespace ?? string.Empty).StartsWith(SystemCollectionsNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        Type? memberType = null;
        if (memberInfo is FieldInfo fieldInfo)
        {
            memberType = fieldInfo.FieldType;
        }
        else
        {
            if (memberInfo is PropertyInfo propertyInfo)
            {
                memberType = propertyInfo.PropertyType;
            }
        }

        if (memberType != null)
        {
            if (typeof(Delegate).IsAssignableFrom(memberType))
            {
                return false;
            }
        }

        return AttributeRegistry.GetAttribute<DataMemberIgnoreAttribute>(memberInfo) is null;
    }
}
