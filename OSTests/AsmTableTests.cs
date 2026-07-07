using System.Reflection;
using System.Text;
using CSharpOS;

namespace OSTests;

/// <summary>
/// §4.1 — the assembler's shared mnemonic/register tables (<see cref="AsmTable"/>). These verify the
/// host-side lookups AND the serialized word-per-char image the ISA `/bin/as` (§4.2) will scan: full
/// opcode coverage, per-shape correctness, register indexing, error sentinels, field sizing, and a
/// round-trip that mirrors the fixed-width ISA record scan.
/// </summary>
public class AsmTableTests
{
    private static int ReadWord(byte[] image, int byteOffset)
    {
        return image[byteOffset] | (image[byteOffset + 1] << 8) | (image[byteOffset + 2] << 16) | (image[byteOffset + 3] << 24);
    }

    // ---- coverage --------------------------------------------------------

    // Every opcode declared in Instruction must be reachable from the table, so adding an opcode
    // without a mnemonic fails here. The two MOV-immediate opcodes have no entry of their own — the
    // single MOV mnemonic (shape Mov) reaches them via MovImm8Opcode/MovImm16Opcode.
    [Fact]
    public void EveryInstructionOpcode_IsReachableFromTheTable()
    {
        HashSet<byte> tableOpcodes = new HashSet<byte>();
        foreach (AsmTable.MnemonicEntry entry in AsmTable.Mnemonics)
        {
            tableOpcodes.Add(entry.Opcode);
        }

        FieldInfo[] opcodeFields = typeof(Instruction)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .ToArray();

        Assert.NotEmpty(opcodeFields);
        foreach (FieldInfo field in opcodeFields)
        {
            byte opcode = (byte)field.GetValue(null)!;
            bool covered = tableOpcodes.Contains(opcode)
                || opcode == AsmTable.MovImm8Opcode
                || opcode == AsmTable.MovImm16Opcode;
            Assert.True(covered, $"Opcode {field.Name} (0x{opcode:X2}) has no assembler mnemonic.");
        }
    }

    [Fact]
    public void EveryTableOpcode_IsADeclaredInstructionOpcode()
    {
        HashSet<byte> declared = typeof(Instruction)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .Select(f => (byte)f.GetValue(null)!)
            .ToHashSet();

        foreach (AsmTable.MnemonicEntry entry in AsmTable.Mnemonics)
        {
            Assert.True(declared.Contains(entry.Opcode), $"Table mnemonic {entry.Mnemonic} maps to an undeclared opcode 0x{entry.Opcode:X2}.");
        }
    }

    [Fact]
    public void MnemonicsAreUnique()
    {
        HashSet<string> seen = new HashSet<string>();
        foreach (AsmTable.MnemonicEntry entry in AsmTable.Mnemonics)
        {
            Assert.True(seen.Add(entry.Mnemonic), $"Duplicate mnemonic {entry.Mnemonic}.");
        }
    }

    // ---- lookups (one representative per shape) ---------------------------

    [Theory]
    [InlineData("MOV", Instruction.MOV_REG_REG, OperandShape.Mov)]
    [InlineData("ADD", Instruction.ADD, OperandShape.RegReg)]
    [InlineData("INC", Instruction.INC, OperandShape.Reg)]
    [InlineData("DREAD", Instruction.DREAD, OperandShape.RegRegReg)]
    [InlineData("JMP", Instruction.JMP, OperandShape.Addr16)]
    [InlineData("HLT", Instruction.HLT, OperandShape.None)]
    [InlineData("OUTS", Instruction.OUTS, OperandShape.RegReg)]
    [InlineData("SIGRETURN", Instruction.SIGRETURN, OperandShape.None)]
    public void TryLookupMnemonic_ResolvesOpcodeAndShape(string mnemonic, byte expectedOpcode, OperandShape expectedShape)
    {
        bool found = AsmTable.TryLookupMnemonic(mnemonic, out byte opcode, out OperandShape shape);

        Assert.True(found);
        Assert.Equal(expectedOpcode, opcode);
        Assert.Equal(expectedShape, shape);
    }

    [Fact]
    public void TryLookupMnemonic_UnknownMnemonic_ReturnsFalse()
    {
        bool found = AsmTable.TryLookupMnemonic("NOPE", out byte opcode, out OperandShape shape);

        Assert.False(found);
        Assert.Equal(0, opcode);
        Assert.Equal(OperandShape.None, shape);
    }

    [Fact]
    public void TryLookupMnemonic_IsCaseSensitive_LowercaseNotFound()
    {
        // The canonical form is UPPERCASE; the tokenizer is responsible for upcasing input.
        Assert.False(AsmTable.TryLookupMnemonic("mov", out _, out _));
    }

    [Fact]
    public void Mov_ImmediateOpcodesAreDistinctFromTheBaseOpcode()
    {
        AsmTable.TryLookupMnemonic("MOV", out byte baseOpcode, out OperandShape shape);

        Assert.Equal(OperandShape.Mov, shape);
        Assert.Equal(Instruction.MOV_REG_REG, baseOpcode);
        Assert.Equal(Instruction.MOV_REG_IMM, AsmTable.MovImm8Opcode);
        Assert.Equal(Instruction.MOV_REG_IMM16, AsmTable.MovImm16Opcode);
    }

    // ---- register table --------------------------------------------------

    [Fact]
    public void RegisterTable_MapsEveryRegisterNameToItsEnumIndex()
    {
        foreach (RegisterName reg in Enum.GetValues<RegisterName>())
        {
            int index = AsmTable.LookupRegister(reg.ToString());
            Assert.Equal((int)reg, index);
        }
    }

    [Theory]
    [InlineData("EAX", 0)]
    [InlineData("ESP", 6)]
    [InlineData("EIP", 8)]
    [InlineData("R8", 16)]
    [InlineData("R15", 23)]
    public void LookupRegister_KnownNames(string name, int expectedIndex)
    {
        Assert.Equal(expectedIndex, AsmTable.LookupRegister(name));
    }

    [Fact]
    public void LookupRegister_UnknownName_ReturnsNotFound()
    {
        Assert.Equal(AsmTable.NotFound, AsmTable.LookupRegister("R16"));
        Assert.Equal(AsmTable.NotFound, AsmTable.LookupRegister("eax"));   // case-sensitive
        Assert.Equal(AsmTable.NotFound, AsmTable.LookupRegister(""));
    }

    [Fact]
    public void RegisterNames_AreIndexOrdered()
    {
        IReadOnlyList<string> names = AsmTable.RegisterNames;
        Assert.Equal(Enum.GetValues<RegisterName>().Length, names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            Assert.Equal(((RegisterName)i).ToString(), names[i]);
        }
    }

    // ---- field sizing (the fixed record widths must hold the longest names) ----

    [Fact]
    public void NoMnemonic_ExceedsTheNameField()
    {
        foreach (AsmTable.MnemonicEntry entry in AsmTable.Mnemonics)
        {
            Assert.True(entry.Mnemonic.Length <= AsmTable.MnemonicMaxChars,
                $"{entry.Mnemonic} exceeds MnemonicMaxChars ({AsmTable.MnemonicMaxChars}).");
        }
    }

    [Fact]
    public void NoRegisterName_ExceedsTheNameField()
    {
        foreach (string name in AsmTable.RegisterNames)
        {
            Assert.True(name.Length <= AsmTable.RegisterMaxChars,
                $"{name} exceeds RegisterMaxChars ({AsmTable.RegisterMaxChars}).");
        }
    }

    // ---- serialized image (mirror the fixed-width ISA record scan) --------

    [Fact]
    public void MnemonicTableImage_RoundTripsEveryEntry_ThenTerminates()
    {
        byte[] image = AsmTable.BuildMnemonicTableImage();
        int recordBytes = AsmTable.MnemonicRecordWords * 4;

        Assert.Equal((AsmTable.Mnemonics.Count + 1) * recordBytes, image.Length);

        for (int i = 0; i < AsmTable.Mnemonics.Count; i++)
        {
            int baseOffset = i * recordBytes;

            StringBuilder name = new StringBuilder();
            for (int w = 0; w < AsmTable.MnemonicNameWords; w++)
            {
                int c = ReadWord(image, baseOffset + w * 4);
                if (c == 0)
                {
                    break;
                }
                name.Append((char)c);
            }
            byte opcode = (byte)ReadWord(image, baseOffset + AsmTable.MnemonicOpcodeWord * 4);
            int shape = ReadWord(image, baseOffset + AsmTable.MnemonicShapeWord * 4);

            AsmTable.MnemonicEntry expected = AsmTable.Mnemonics[i];
            Assert.Equal(expected.Mnemonic, name.ToString());
            Assert.Equal(expected.Opcode, opcode);
            Assert.Equal((int)expected.Shape, shape);
        }

        // Terminator record: its first name word is 0.
        int terminatorBase = AsmTable.Mnemonics.Count * recordBytes;
        Assert.Equal(0, ReadWord(image, terminatorBase));
    }

    [Fact]
    public void MnemonicTableImage_NameFieldIsNullTerminatedAfterEachName()
    {
        byte[] image = AsmTable.BuildMnemonicTableImage();
        int recordBytes = AsmTable.MnemonicRecordWords * 4;

        for (int i = 0; i < AsmTable.Mnemonics.Count; i++)
        {
            int baseOffset = i * recordBytes;
            int nameLen = AsmTable.Mnemonics[i].Mnemonic.Length;
            // The word right after the last char (within the name field) must be the null terminator.
            if (nameLen < AsmTable.MnemonicNameWords)
            {
                Assert.Equal(0, ReadWord(image, baseOffset + nameLen * 4));
            }
        }
    }

    [Fact]
    public void RegisterTableImage_RoundTripsEveryEntry_ThenTerminates()
    {
        byte[] image = AsmTable.BuildRegisterTableImage();
        int recordBytes = AsmTable.RegisterRecordWords * 4;

        Assert.Equal((AsmTable.RegisterNames.Count + 1) * recordBytes, image.Length);

        for (int i = 0; i < AsmTable.RegisterNames.Count; i++)
        {
            int baseOffset = i * recordBytes;

            StringBuilder name = new StringBuilder();
            for (int w = 0; w < AsmTable.RegisterNameWords; w++)
            {
                int c = ReadWord(image, baseOffset + w * 4);
                if (c == 0)
                {
                    break;
                }
                name.Append((char)c);
            }
            int index = ReadWord(image, baseOffset + AsmTable.RegisterIndexWord * 4);

            Assert.Equal(AsmTable.RegisterNames[i], name.ToString());
            Assert.Equal(i, index);
        }

        int terminatorBase = AsmTable.RegisterNames.Count * recordBytes;
        Assert.Equal(0, ReadWord(image, terminatorBase));
    }
}
