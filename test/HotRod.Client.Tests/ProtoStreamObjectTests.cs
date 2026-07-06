using HotRod.Client.Marshalling;

namespace HotRod.Client.Tests;

/// <summary>
/// Registering an <see cref="IProtoStreamMarshaller{T}"/> lets a custom type ride the wire as a
/// Protobuf <c>WrappedMessage</c>: the type name in field 16 and the hand-marshalled body in field 17.
/// These tests round-trip such a type end to end, pin that wrapper structure, and check that the
/// field reader/writer handle each scalar and skip unknown fields.
/// </summary>
public class ProtoStreamObjectTests
{
    private sealed record Person(string Name, int Age, double Height);

    /// <summary>A hand-written marshaller mapping <see cref="Person"/> to the <c>tutorial.Person</c> message.</summary>
    private sealed class PersonMarshaller : IProtoStreamMarshaller<Person>
    {
        public string TypeName => "tutorial.Person";

        public void WriteTo(ProtoStreamWriter writer, Person value)
        {
            writer.WriteString(1, value.Name);
            writer.WriteInt32(2, value.Age);
            writer.WriteDouble(3, value.Height);
        }

        public Person ReadFrom(ProtoStreamReader reader)
        {
            string name = "";
            int age = 0;
            double height = 0;
            while (reader.ReadTag(out int field))
            {
                switch (field)
                {
                    case 1: name = reader.ReadString(); break;
                    case 2: age = reader.ReadInt32(); break;
                    case 3: height = reader.ReadDouble(); break;
                    default: reader.SkipField(); break;
                }
            }
            return new Person(name, age, height);
        }
    }

    private static SerializationContext ContextWithPerson() =>
        new SerializationContext().Register(new PersonMarshaller());

    [Fact]
    public void Round_trips_a_registered_custom_type()
    {
        SerializationContext context = ContextWithPerson();
        var alice = new Person("Alice", 30, 1.75);

        byte[] wire = context.Marshal(alice);
        Person back = context.Unmarshal<Person>(wire);

        Assert.Equal(alice, back);
    }

    [Fact]
    public void Wraps_the_type_name_in_field_16_and_the_body_in_field_17()
    {
        SerializationContext context = ContextWithPerson();
        byte[] wire = context.Marshal(new Person("Bob", 41, 1.8));

        var reader = new ProtoStreamReader(wire);
        string? typeName = null;
        byte[]? body = null;
        while (reader.ReadTag(out int field))
        {
            switch (field)
            {
                case 16: typeName = reader.ReadString(); break;
                case 17: body = reader.ReadBytes(); break;
                default: reader.SkipField(); break;
            }
        }

        Assert.Equal("tutorial.Person", typeName);
        Assert.NotNull(body);

        // The body is the plain message: field 1 = name, field 2 = age, field 3 = height.
        var bodyReader = new ProtoStreamReader(body!);
        Assert.True(bodyReader.ReadTag(out int f1));
        Assert.Equal(1, f1);
        Assert.Equal("Bob", bodyReader.ReadString());
        Assert.True(bodyReader.ReadTag(out int f2));
        Assert.Equal(2, f2);
        Assert.Equal(41, bodyReader.ReadInt32());
    }

    [Fact]
    public void Reader_skips_unknown_fields()
    {
        // A writer that adds a field the reader does not recognize (field 9) between known ones.
        var writer = new ProtoStreamWriter();
        writer.WriteString(1, "Carol");
        writer.WriteString(9, "unexpected");
        writer.WriteInt32(2, 22);
        writer.WriteDouble(3, 1.6);

        Person person = new PersonMarshaller().ReadFrom(new ProtoStreamReader(writer.ToArray()));

        Assert.Equal(new Person("Carol", 22, 1.6), person);
    }

    [Fact]
    public void Writer_and_reader_round_trip_each_scalar()
    {
        var writer = new ProtoStreamWriter();
        writer.WriteInt32(1, -13);
        writer.WriteInt64(2, 9_000_000_000L);
        writer.WriteUInt32(3, 4_000_000_000u);
        writer.WriteBool(4, true);
        writer.WriteDouble(5, 2.71828d);
        writer.WriteFloat(6, 0.5f);
        writer.WriteString(7, "π");
        writer.WriteBytes(8, [9, 8, 7]);

        var reader = new ProtoStreamReader(writer.ToArray());
        Read(reader, 1); Assert.Equal(-13, reader.ReadInt32());
        Read(reader, 2); Assert.Equal(9_000_000_000L, reader.ReadInt64());
        Read(reader, 3); Assert.Equal(4_000_000_000u, reader.ReadUInt32());
        Read(reader, 4); Assert.True(reader.ReadBool());
        Read(reader, 5); Assert.Equal(2.71828d, reader.ReadDouble());
        Read(reader, 6); Assert.Equal(0.5f, reader.ReadFloat());
        Read(reader, 7); Assert.Equal("π", reader.ReadString());
        Read(reader, 8); Assert.Equal(new byte[] { 9, 8, 7 }, reader.ReadBytes());
        Assert.False(reader.ReadTag(out _));
    }

    [Fact]
    public void An_unregistered_custom_type_falls_through_to_the_primitive_marshaller_and_is_rejected()
    {
        // Default has no Person marshaller, so encoding one is unsupported.
        Assert.Throws<HotRodException>(() => SerializationContext.Default.Marshal(new Person("x", 1, 1)));
    }

    private static void Read(ProtoStreamReader reader, int expectedField)
    {
        Assert.True(reader.ReadTag(out int field));
        Assert.Equal(expectedField, field);
    }
}
