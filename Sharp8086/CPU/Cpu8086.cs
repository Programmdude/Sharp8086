﻿using System;
using System.Diagnostics;
using System.IO;
using Sharp8086.Core;
using Sharp8086.Peripheral.IO;

namespace Sharp8086.CPU
{
    public sealed class Cpu8086 : ICpu, IInstructionFetcher
    {
        public enum Register : uint
        {
            AX = 0,
            CX = 1,
            DX = 2,
            BX = 3,

            SP = 4,
            BP = 5,
            SI = 6,
            DI = 7,

            ES = 8,
            CS = 9,
            SS = 10,
            DS = 11,

            IP = 12,

            AL = 0x80000000 | 0,
            CL = 0x80000000 | 1,
            DL = 0x80000000 | 2,
            BL = 0x80000000 | 3,
            AH = 0x80000000 | 4,
            CH = 0x80000000 | 5,
            DH = 0x80000000 | 6,
            BH = 0x80000000 | 7,

            FLAGS = 0x80000000 | 0xFF,

            Invalid = 0xFFFFFFFF
        }

        [Flags]
        public enum FlagsRegister : ushort
        {
            Carry = 1 << 0,
            Parity = 1 << 2,
            Auxiliary = 1 << 4,
            Zero = 1 << 6,
            Sign = 1 << 7,
            Trap = 1 << 8,
            Interrupt = 1 << 9,
            Direction = 1 << 10,
            Overflow = 1 << 11
        }

        private delegate void InstructionDispatch(Cpu8086 cpu, OpCodeManager.Instruction instruction);

        public const int PAGE_SHIFT = 12;
        private const int IO_PORT_OFFSET = 0xE0000;
        private const int IO_PORT_SIZE = 0x10000;

        private static readonly bool[] parityLookup =
        {
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true,
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true,
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            false, true, true, false, true, false, false, true, true, false, false, true, false, true, true, false,
            true, false, false, true, false, true, true, false, false, true, true, false, true, false, false, true
        };

        private static readonly InstructionDispatch[] dispatches =
        {
            DispatchInvalid,

            DispatchArithmetic,
            DispatchPush,
            DispatchPop,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchInvalid,
            DispatchDaa,
            DispatchDas,
            DispatchAaa,
            DispatchAas,
            DispatchUnaryArithmetic,
            DispatchUnaryArithmetic,
            DispatchJumpRelative,
            DispatchJump,
            DispatchFarJump,
            DispatchInvalid,
            DispatchArithmetic,
            DispatchExchange,
            DispatchMove,
            DispatchLea,
            DispatchCbw,
            DispatchCwd,
            DispatchCallNearRelative,
            DispatchCallNear,
            DispatchCallFar,
            DispatchWait,
            DispatchSahf,
            DispatchLahf,
            DispatchStringOperation,
            DispatchStringOperation,
            DispatchStringOperation,
            DispatchStringOperation,
            DispatchStringOperation,
            DispatchReturnNear,
            DispatchReturnFar,
            (cpu, instruction) => DispatchLoadFarPointer(cpu, instruction, Register.ES),
            (cpu, instruction) => DispatchLoadFarPointer(cpu, instruction, Register.DS),
            DispatchInterrupt,
            DispatchInto,
            DispatchReturnInterrupt,
            DispatchAam,
            DispatchAad,
            DispatchXlat,
            DispatchLoopNotZero,
            DispatchLoopZero,
            DispatchLoop,
            DispatchClc,
            DispatchStc,
            DispatchJcxz,
            DispatchIn,
            DispatchOut,
            DispatchHalt,
            DispatchCmc,
            DispatchCli,
            DispatchSti,
            DispatchCld,
            DispatchStd,
            DispatchJumpIfOverflow,
            DispatchJumpIfNotOverflow,
            DispatchJumpIfCarry,
            DispatchJumpIfNotCarry,
            DispatchJumpIfZero,
            DispatchJumpIfNotZero,
            DispatchJBE,
            DispatchJA,
            DispatchJS,
            DispatchJNS,
            DispatchJPE,
            DispatchJPO,
            DispatchJL,
            DispatchJGE,
            DispatchJLE,
            DispatchJG,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchArithmetic,
            DispatchUnaryArithmetic,
            DispatchUnaryArithmetic,
            DispatchMultiply,
            DispatchMultiply,
            DispatchDivide,
            DispatchSignedDivide,
            DispatchEmulatorSpecial
        };

        private const FlagsRegister FLAGS_MASK = FlagsRegister.Carry |
                                                 FlagsRegister.Parity |
                                                 FlagsRegister.Auxiliary |
                                                 FlagsRegister.Zero |
                                                 FlagsRegister.Sign |
                                                 FlagsRegister.Trap |
                                                 FlagsRegister.Interrupt |
                                                 FlagsRegister.Direction |
                                                 FlagsRegister.Overflow;
        private readonly IPageController[] pages;
        private readonly IDrive[] drives = new IDrive[0x100];
        private readonly IOPageController ioController;
        private readonly ushort[] registers = new ushort[13];
        private DateTime lastBiosTime = DateTime.Now;
        private FlagsRegister flags;

        public Cpu8086(Stream biosFile, uint memorySize)
        {
            var numberPages = memorySize >> PAGE_SHIFT;
            pages = new IPageController[numberPages];
            var memory = new byte[memorySize];
            for (var i = 0u; i != memorySize; i++)
                memory[i] = 0xCC;

            var defaultPageController = new ConventionPageController(memory);
            for (var i = 0; i < numberPages; i++)
                pages[i] = defaultPageController;

            const int ioPortOffsetPage = IO_PORT_OFFSET >> PAGE_SHIFT;
            ioController = new IOPageController(IO_PORT_OFFSET, IO_PORT_SIZE);
            for (var i = 0; i < IO_PORT_SIZE >> PAGE_SHIFT; i++)
                pages[ioPortOffsetPage + i] = ioController;

            if (biosFile.Length > 0x10000)
                throw new InvalidDataException();
            biosFile.Read(memory, 0xF0000 + (0x10000 - (int)biosFile.Length), (int)biosFile.Length);

            WriteU8(0x410, 0x41); //ugly hack to make BIOS always believe we have an EGA/VGA card installed
            WriteU8(0x475, 0); //the BIOS doesn't have any concept of hard drives, so here's another hack

            SetRegister(Register.CS, 0xF000);
            SetRegister(Register.IP, 0xFFF0);
        }

        public bool ProcessInstructions(int amount)
        {
            for (var i = 0; i < amount; i++)
            {
                if (!ProcessSingleInstruction())
                    return false;
            }
            return true;
        }

        private static string[] oplist =
        {
            "Add",
            "Add",
            "Add",
            "Add",
            "Add",
            "Add",
            "Push",
            "Pop",
            "Or",
            "Or",
            "Or",
            "Or",
            "Or",
            "Or",
            "Push",
            "EmulatorSpecial",
            "Adc",
            "Adc",
            "Adc",
            "Adc",
            "Adc",
            "Adc",
            "Push",
            "Pop",
            "Sbb",
            "Sbb",
            "Sbb",
            "Sbb",
            "Sbb",
            "Sbb",
            "Push",
            "Pop",
            "And",
            "And",
            "And",
            "And",
            "And",
            "And",
            "Prefix",
            "Daa",
            "Subtract",
            "Subtract",
            "Subtract",
            "Subtract",
            "Subtract",
            "Subtract",
            "Prefix",
            "Das",
            "Xor",
            "Xor",
            "Xor",
            "Xor",
            "Xor",
            "Xor",
            "Prefix",
            "Aaa",
            "Compare",
            "Compare",
            "Compare",
            "Compare",
            "Compare",
            "Compare",
            "Prefix",
            "Aas",
            "Increment",
            "Increment",
            "Increment",
            "Increment",
            "Increment",
            "Increment",
            "Increment",
            "Increment",
            "Decrement",
            "Decrement",
            "Decrement",
            "Decrement",
            "Decrement",
            "Decrement",
            "Decrement",
            "Decrement",
            "Push",
            "Push",
            "Push",
            "Push",
            "Push",
            "Push",
            "Push",
            "Push",
            "Pop",
            "Pop",
            "Pop",
            "Pop",
            "Pop",
            "Pop",
            "Pop",
            "Pop",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "JO",
            "JNO",
            "JB",
            "JNB",
            "JZ",
            "JNZ",
            "JBE",
            "JA",
            "JS",
            "JNS",
            "JPE",
            "JPO",
            "JL",
            "JGE",
            "JLE",
            "JG",
            "Group",
            "Group",
            "Group",
            "Group",
            "Test",
            "Test",
            "Xchg",
            "Xchg",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Lea",
            "Move",
            "Pop",
            "Xchg",
            "Xchg",
            "Xchg",
            "Xchg",
            "Xchg",
            "Xchg",
            "Xchg",
            "Xchg",
            "Cbw",
            "Cwd",
            "CallFar",
            "Wait",
            "Push",
            "Pop",
            "Sahf",
            "Lahf",
            "Move",
            "Move",
            "Move",
            "Move",
            "Movs",
            "Movs",
            "Cmps",
            "Cmps",
            "Test",
            "Test",
            "Stos",
            "Stos",
            "Lods",
            "Lods",
            "Scas",
            "Scas",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Move",
            "Group",
            "Group",
            "ReturnNear",
            "ReturnNear",
            "Les",
            "Lds",
            "Move",
            "Move",
            "Invalid",
            "Invalid",
            "ReturnFar",
            "ReturnFar",
            "Int",
            "Int",
            "Into",
            "ReturnInterrupt",
            "Group",
            "Group",
            "Group",
            "Group",
            "Aam",
            "Aad",
            "Invalid",
            "Xlat",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Invalid",
            "Loopnz",
            "Loopz",
            "Loop",
            "Jcxz",
            "In",
            "In",
            "Out",
            "Out",
            "CallNearRelative",
            "JumpRelative",
            "JumpFar",
            "JumpRelative",
            "In",
            "In",
            "Out",
            "Out",
            "Prefix",
            "Invalid",
            "Prefix",
            "Prefix",
            "Hlt",
            "Cmc",
            "Group",
            "Group",
            "Clc",
            "Stc",
            "Cli",
            "Sti",
            "Cld",
            "Std",
            "Group",
            "Group"
        };

        //private TextWriter z = new StreamWriter(File.Open("dmp.txt", FileMode.Create));

        public bool ProcessSingleInstruction()
        {
            var savecs = GetRegister(Register.CS);
            var saveip = GetRegister(Register.IP);
            // if (savecs != 0xF000)
            // {
            //     byte opcode;
            //     do
            //     {
            //         opcode = ReadU8(SegmentToAddress(savecs, saveip));
            //         saveip++;
            //     }
            //     while (opcode == 0x26 ||
            //            opcode == 0x2E ||
            //            opcode == 0x36 ||
            //            opcode == 0x3E ||
            //            opcode == 0xF0 ||
            //            opcode == 0xF1 ||
            //            opcode == 0xF2);
            //     saveip--;
            //
            //     z.Write($"{savecs:X4}:{saveip:X4} - {oplist[opcode]}\n");
            //     z.Write($"AX: {GetRegister(Register.AX):X4}, CX: {GetRegister(Register.CX):X4}, DX: {GetRegister(Register.DX):X4}, BX: {GetRegister(Register.BX):X4}, SP: {GetRegister(Register.SP):X4}, BP: {GetRegister(Register.BP):X4}, SI: {GetRegister(Register.SI):X4}, DI: {GetRegister(Register.DI):X4}\n");
            //     z.Write($"ES: {GetRegister(Register.ES):X4}, CS: {GetRegister(Register.CS):X4}, SS: {GetRegister(Register.SS):X4}, DS: {GetRegister(Register.DS):X4}, FLAGS: {GetRegister(Register.FLAGS) | 0xF002:X4}\n");
            //     z.Flush();
            // }

            //if (GetRegister(Register.CS) == 0x0060 && GetRegister(Register.IP) == 0x001D)
            //    Debugger.Break();

            string instructionText = $"{GetRegister(Register.CS):X4}:{GetRegister(Register.IP):X4} ";
            var instruction = OpCodeManager.Decode(this);
            instructionText += OutputInstruction(instruction);

            //if (GetRegister(Register.CS) != 0xF000)
            //if (instructionText.ToLower().Contains("ret") || instructionText.ToLower().Contains("push") || instructionText.ToLower().Contains("pop") || instructionText.ToLower().Contains("call") || instructionText.ToLower().Contains("int") || instructionText.ToLower().Contains("sp"))
                //Console.WriteLine(instructionText + " " + GetRegister(Register.SP));
                //Console.WriteLine(instructionText);

            dispatches[(int)instruction.Type](this, instruction);
            return instruction.Type != OpCodeManager.InstructionType.Hlt;
        }
        public void AttachDevice(IDevice device)
        {
            var hasUse = false;

            var drive = device as IDrive;
            if (drive != null)
            {
                var hdd = drive.FloppyType == 0;
                int i;
                for (i = 0; i < 0x80; i++)
                {
                    if (drives[i + (hdd ? 0x80 : 0)] == null)
                    {
                        drives[i + (hdd ? 0x80 : 0)] = drive;
                        break;
                    }
                }
                if (i == 0x80)
                    throw new NotImplementedException();
                hasUse = true;
            }

            var memoryMappedDevice = device as IMemoryMappedDevice;
            if (memoryMappedDevice != null)
            {
                foreach (var memory in memoryMappedDevice.MappedMemory)
                {
                    var startPage = memory.Item1;
                    var numberPages = memory.Item2;
                    for (var i = 0; i < numberPages; i++)
                        pages[startPage + i] = memoryMappedDevice;
                }

                hasUse = true;
            }

            var ioMappedDevice = device as IIOMappedDevice;
            if (ioMappedDevice != null)
            {
                foreach (var port in ioMappedDevice.MappedPorts)
                    this.ioController[port] = ioMappedDevice;

                hasUse = true;
            }

            if (!hasUse)
                throw new NotImplementedException();
        }
        private ushort ProcessExchangeSecond(OpCodeManager.Instruction instruction, ushort value)
        {
            ushort tmp;
            switch (instruction.Argument2)
            {
                case (int)Register.AX:
                case (int)Register.CX:
                case (int)Register.DX:
                case (int)Register.BX:
                case (int)Register.SP:
                case (int)Register.BP:
                case (int)Register.SI:
                case (int)Register.DI:
                case (int)Register.IP:
                case (int)Register.CS:
                case (int)Register.DS:
                case (int)Register.ES:
                case (int)Register.SS:
                    tmp = registers[instruction.Argument2];
                    registers[instruction.Argument2] = value;
                    break;

                case OpCodeManager.ARG_BYTE_REGISTER:
                    tmp = GetRegisterU8((Register)instruction.Argument2Value);
                    SetRegisterU8((Register)instruction.Argument2Value, (byte)value);
                    break;

                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    var address = GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);
                    tmp = ReadU16(address);
                    WriteU16(address, value);
                    break;

                default:
                    throw new NotImplementedException();
            }
            return tmp;
        }
        private void Push(ushort value)
        {
            registers[(int)Register.SP] -= 2;
            WriteU16(SegmentToAddress(GetRegister(Register.SS), GetRegister(Register.SP)), value);
        }
        private ushort Pop()
        {
            var value = ReadU16(SegmentToAddress(GetRegister(Register.SS), GetRegister(Register.SP)));
            registers[(int)Register.SP] += 2;
            return value;
        }
        private uint GetInstructionRealAddress(Register segmentPrefix, int instruction, int instructionValue, int instructionDisplacement)
        {
            switch (instruction)
            {
                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    if (segmentPrefix == Register.Invalid) segmentPrefix = Register.DS;
                    var address = GetInstructionAddress(instruction, instructionValue, instructionDisplacement);
                    return SegmentToAddress(GetRegister(segmentPrefix), address);

                default:
                    throw new NotImplementedException();
            }
        }
        private ushort GetInstructionAddress(int instruction, int instructionValue, int instructionDisplacement)
        {
            switch (instruction)
            {
                case OpCodeManager.ARG_DEREFERENCE:
                    ushort address;
                    switch (instructionValue)
                    {
                        case 0:
                            address = (ushort)(GetRegister(Register.BX) + GetRegister(Register.SI));
                            break;
                        case 1:
                            address = (ushort)(GetRegister(Register.BX) + GetRegister(Register.DI));
                            break;
                        case 2:
                            address = (ushort)(GetRegister(Register.BP) + GetRegister(Register.SI));
                            break;
                        case 3:
                            address = (ushort)(GetRegister(Register.BP) + GetRegister(Register.DI));
                            break;
                        case 4:
                            address = GetRegister(Register.SI);
                            break;
                        case 5:
                            address = GetRegister(Register.DI);
                            break;
                        case 6:
                            address = GetRegister(Register.BP);
                            break;
                        case 7:
                            address = GetRegister(Register.BX);
                            break;
                        default:
                            throw new NotImplementedException();
                    }
                    return (ushort)(address + instructionDisplacement);

                case OpCodeManager.ARG_MEMORY:
                    return (ushort)instructionValue;

                default:
                    throw new NotImplementedException();
            }
        }
        private ushort GetInstructionValue(OpCodeManager.OpCodeFlag flag, Register segmentPrefix, int instruction, int instructionValue, int instructionDisplacement)
        {
            switch (instruction)
            {
                case (int)Register.AX:
                case (int)Register.CX:
                case (int)Register.DX:
                case (int)Register.BX:
                case (int)Register.SP:
                case (int)Register.BP:
                case (int)Register.SI:
                case (int)Register.DI:
                case (int)Register.IP:
                case (int)Register.CS:
                case (int)Register.DS:
                case (int)Register.ES:
                case (int)Register.SS:
                    return GetRegister((Register)instruction);

                case OpCodeManager.ARG_BYTE_REGISTER:
                    return GetRegisterU8((Register)instructionValue);

                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    var address = GetInstructionRealAddress(segmentPrefix, instruction, instructionValue, instructionDisplacement);
                    return flag.Has(OpCodeManager.OpCodeFlag.Size8) ? ReadU8(address) : ReadU16(address);

                case OpCodeManager.ARG_CONSTANT:
                    return (ushort)instructionValue;

                default:
                    throw new NotImplementedException();
            }
        }
        private void Interrupt(byte interrupt)
        {
            if (interrupt == 0x03)
                Debugger.Break();

            Push(GetRegister(Register.FLAGS));
            Push(GetRegister(Register.CS));
            Push(GetRegister(Register.IP));
            SetRegister(Register.IP, ReadU16((uint)interrupt * 4));
            SetRegister(Register.CS, ReadU16((uint)interrupt * 4 + 2));
        }

        private void CalculateIncFlags(OpCodeManager.OpCodeFlag flag, ushort value1, ushort value2, int result)
        {
            if (flag.Has(OpCodeManager.OpCodeFlag.Size8))
                CalculateIncFlags8Bit((byte)value1, (byte)value2, result);
            else CalculateIncFlags16Bit(value1, value2, result);
        }
        private void CalculateDecFlags(OpCodeManager.OpCodeFlag flag, ushort value1, ushort value2, int result)
        {
            if (flag.Has(OpCodeManager.OpCodeFlag.Size8))
                CalculateDecFlags8Bit((byte)value1, (byte)value2, result);
            else CalculateDecFlags16Bit(value1, value2, result);
        }
        private void CalculateBitwiseFlags(OpCodeManager.OpCodeFlag flag, ushort value1, ushort value2, int result)
        {
            if (flag.Has(OpCodeManager.OpCodeFlag.Size8))
                CalculateBitwiseFlags8Bit((byte)value1, (byte)value2, result);
            else CalculateBitwiseFlags16Bit(value1, value2, result);
        }
        private void CalculateAddFlags(OpCodeManager.OpCodeFlag flag, ushort value1, ushort value2, int result)
        {
            if (flag.Has(OpCodeManager.OpCodeFlag.Size8))
                CalculateAddFlags8Bit((byte)value1, (byte)value2, result);
            else CalculateAddFlags16Bit(value1, value2, result);
        }
        private void CalculateSubFlags(OpCodeManager.OpCodeFlag flag, ushort value1, ushort value2, int result)
        {
            if (flag.Has(OpCodeManager.OpCodeFlag.Size8))
                CalculateSubFlags8Bit((byte)value1, (byte)value2, result);
            else CalculateSubFlags16Bit(value1, value2, result);
        }

        private void CalculateIncFlags8Bit(byte value1, byte value2, int result)
        {
            var truncResult = (byte)result;
            var sign = ((truncResult >> 7) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (truncResult ^ value2) & 0x80) == 0x80;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }
        private void CalculateIncFlags16Bit(ushort value1, ushort value2, int result)
        {
            var truncResult = (ushort)result;
            var sign = ((truncResult >> 15) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (truncResult ^ value2) & 0x8000) == 0x8000;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }
        private void CalculateDecFlags8Bit(byte value1, byte value2, int result)
        {
            var truncResult = (byte)result;
            var sign = ((truncResult >> 7) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (value1 ^ value2) & 0x80) == 0x80;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }
        private void CalculateDecFlags16Bit(ushort value1, ushort value2, int result)
        {
            var truncResult = (ushort)result;
            var sign = ((truncResult >> 15) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (value1 ^ value2) & 0x8000) == 0x8000;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }
        private void CalculateBitwiseFlags8Bit(byte value1, byte value2, int result)
        {
            var truncResult = (byte)result;
            var sign = ((truncResult >> 7) & 1) == 1;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Carry | FlagsRegister.Parity | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (parity ? FlagsRegister.Parity : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0);
        }
        private void CalculateBitwiseFlags16Bit(ushort value1, ushort value2, int result)
        {
            var truncResult = (ushort)result;
            var sign = ((truncResult >> 15) & 1) == 1;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Carry | FlagsRegister.Parity | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (parity ? FlagsRegister.Parity : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0);
        }
        private void CalculateAddFlags8Bit(byte value1, byte value2, int result)
        {
            var truncResult = (byte)result;
            var carry = (uint)result > 0xFF;
            var sign = ((truncResult >> 7) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (truncResult ^ value2) & 0x80) == 0x80;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Carry | FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (carry ? FlagsRegister.Carry : 0) |
                     (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }
        private void CalculateAddFlags16Bit(ushort value1, ushort value2, int result)
        {
            var truncResult = (ushort)result;
            var carry = (uint)result > 0xFFFF;
            var sign = ((truncResult >> 15) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (truncResult ^ value2) & 0x8000) == 0x8000;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Carry | FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (carry ? FlagsRegister.Carry : 0) |
                     (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }
        private void CalculateSubFlags8Bit(byte value1, byte value2, int result)
        {
            var truncResult = (byte)result;
            var carry = (uint)result > 0xFF;
            var sign = ((truncResult >> 7) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (value1 ^ value2) & 0x80) == 0x80;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Carry | FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (carry ? FlagsRegister.Carry : 0) |
                     (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }
        private void CalculateSubFlags16Bit(ushort value1, ushort value2, int result)
        {
            var truncResult = (ushort)result;
            var carry = (uint)result > 0xFFFF;
            var sign = ((truncResult >> 15) & 1) == 1;
            var overflow = ((truncResult ^ value1) & (value1 ^ value2) & 0x8000) == 0x8000;
            var auxiliary = ((value1 ^ value2 ^ truncResult) & 0x10) != 0;
            var zero = truncResult == 0;
            var parity = parityLookup[(byte)(result & 0xFF)];

            flags &= ~(FlagsRegister.Carry | FlagsRegister.Parity | FlagsRegister.Auxiliary | FlagsRegister.Zero | FlagsRegister.Sign | FlagsRegister.Overflow);
            flags |= (carry ? FlagsRegister.Carry : 0) |
                     (parity ? FlagsRegister.Parity : 0) |
                     (auxiliary ? FlagsRegister.Auxiliary : 0) |
                     (zero ? FlagsRegister.Zero : 0) |
                     (sign ? FlagsRegister.Sign : 0) |
                     (overflow ? FlagsRegister.Overflow : 0);
        }

        private BiosData CreateBiosData()
        {
            BiosData data;
            data.SerialPort1 = 0x03F8;
            data.SerialPort2 = 0x02F8;
            data.SerialPort3 = 0x03E8;
            data.SerialPort4 = 0x02E8;
            data.ParallelPort1 = 0x03BC;
            data.ParallelPort2 = 0x0378;
            data.ParallelPort3 = 0x0278;
            data.MemorySize = 640;

            data.Equipment = 0;
            if (drives[0] != null)
                data.Equipment |= 1 << 0; // Has boot floppy
            // data.Equipment |= 1 << 1; // Maths coprocessor
            // data.Equipment |= 1 << 2; // PS2 Mouse
            if (drives[0] != null && drives[1] != null)
                data.Equipment |= 1 << 6;

            return data;
        }

        private static void DispatchInvalid(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            throw new NotImplementedException();
        }
        private static void DispatchMove(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var value = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);

            switch (instruction.Argument1)
            {
                case (int)Register.AX:
                case (int)Register.CX:
                case (int)Register.DX:
                case (int)Register.BX:
                case (int)Register.SP:
                case (int)Register.BP:
                case (int)Register.SI:
                case (int)Register.DI:
                case (int)Register.IP:
                case (int)Register.CS:
                case (int)Register.DS:
                case (int)Register.ES:
                case (int)Register.SS:
                    cpu.registers[instruction.Argument1] = value;
                    break;

                case OpCodeManager.ARG_BYTE_REGISTER:
                    cpu.SetRegisterU8((Register)instruction.Argument1Value, (byte)value);
                    break;

                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    var address = cpu.GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                        cpu.WriteU8(address, (byte)value);
                    else cpu.WriteU16(address, value);
                    break;

                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchArithmetic(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var value1 = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
            var value2 = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);

            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
            {
                value1 &= 0xFF;
                value2 &= 0xFF;
            }

            int result;
            bool carry;
            switch (instruction.Type)
            {
                case OpCodeManager.InstructionType.Adc:
                    result = value1 + value2 + (cpu.GetFlags().Has(FlagsRegister.Carry) ? 1 : 0);
                    cpu.CalculateAddFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Add:
                    result = value1 + value2;
                    cpu.CalculateAddFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.And:
                case OpCodeManager.InstructionType.Test:
                    result = value1 & value2;
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Compare:
                case OpCodeManager.InstructionType.Subtract:
                    result = value1 - value2;
                    cpu.CalculateSubFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Or:
                    result = value1 | value2;
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Rcl:
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                    {
                        const int mask = 0x1FF;
                        var shift = (value2 & 0x1F) % 9;

                        result = (byte)value1;
                        if (cpu.flags.Has(FlagsRegister.Carry))
                            result |= 0x100;
                        result = (byte)(result << shift) | (byte)(result >> (-shift & mask));
                        if ((result & 0x100) != 0)
                            cpu.flags |= FlagsRegister.Carry;
                        else cpu.flags &= ~FlagsRegister.Carry;

                        if (value2 == 1)
                        {
                            if (((result & 0x100) != 0) ^ ((result & 0x80) != 0))
                                cpu.flags |= FlagsRegister.Overflow;
                            else cpu.flags &= ~FlagsRegister.Overflow;
                        }
                    }
                    else
                    {
                        const int mask = 0x1FFFF;
                        var shift = (value2 & 0x1F) % 17;

                        result = value1;
                        if (cpu.flags.Has(FlagsRegister.Carry))
                            result |= 0x10000;
                        result = (ushort)(result << shift) | (ushort)(result >> (-shift & mask));
                        if ((result & 0x10000) != 0)
                            cpu.flags |= FlagsRegister.Carry;
                        else cpu.flags &= ~FlagsRegister.Carry;

                        if (value2 == 1)
                        {
                            if (((result & 0x10000) != 0) ^ ((result & 0x8000) != 0))
                                cpu.flags |= FlagsRegister.Overflow;
                            else cpu.flags &= ~FlagsRegister.Overflow;
                        }
                    }
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Rcr:
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                    {
                        const int mask = 0x1FF;
                        var shift = (value2 & 0x1F) % 9;

                        result = (byte)value1;
                        if (cpu.flags.Has(FlagsRegister.Carry))
                            result |= 0x100;
                        result = (byte)(result >> shift) | (byte)(result << (-shift & mask));
                        if ((result & 0x100) != 0)
                            cpu.flags |= FlagsRegister.Carry;
                        else cpu.flags &= ~FlagsRegister.Carry;

                        if (value2 == 1)
                        {
                            if (((result & 0x80) != 0) ^ ((result & 0x40) != 0))
                                cpu.flags |= FlagsRegister.Overflow;
                            else cpu.flags &= ~FlagsRegister.Overflow;
                        }
                    }
                    else
                    {
                        const int mask = 0x1FFFF;
                        var shift = (value2 & 0x1F) % 17;

                        result = value1;
                        if (cpu.flags.Has(FlagsRegister.Carry))
                            result |= 0x10000;
                        result = (ushort)(result >> shift) | (ushort)(result << (-shift & mask));
                        if ((result & 0x10000) != 0)
                            cpu.flags |= FlagsRegister.Carry;
                        else cpu.flags &= ~FlagsRegister.Carry;

                        if (value2 == 1)
                        {
                            if (((result & 0x8000) != 0) ^ ((result & 0x4000) != 0))
                                cpu.flags |= FlagsRegister.Overflow;
                            else cpu.flags &= ~FlagsRegister.Overflow;
                        }
                    }
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Rol:
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                    {
                        const int mask = 0xFF;
                        var shift = value2 & mask;
                        result = (byte)(value1 << shift) | (byte)(value1 >> (-shift & mask));
                    }
                    else
                    {
                        const int mask = 0xFFFF;
                        var shift = value2 & mask;
                        result = (ushort)(value1 << shift) | (ushort)(value1 >> (-shift & mask));
                    }
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Ror:
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                    {
                        const int mask = 0xFF;
                        var shift = value2 & mask;
                        result = (byte)(value1 >> shift) | (byte)(value1 << (-shift & mask));
                    }
                    else
                    {
                        const int mask = 0xFFFF;
                        var shift = value2 & mask;
                        result = (ushort)(value1 >> shift) | (ushort)(value1 << (-shift & mask));
                    }
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Sbb:
                    result = value1 - (value2 + (cpu.GetFlags().Has(FlagsRegister.Carry) ? 1 : 0));
                    cpu.CalculateSubFlags(instruction.Flag, value1, value2, result);
                    break;

                case OpCodeManager.InstructionType.Shl:
                    bool overflow;
                    result = value1 << (value2 & 0x1F);
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, (ushort)(value2 & 0x1F), result);
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                    {
                        carry = (result & 0x100) != 0;
                        overflow = (result & 0x80) != 0;
                    }
                    else
                    {
                        carry = (result & 0x10000) != 0;
                        overflow = (result & 0x8000) != 0;
                    }
                    if (carry)
                        cpu.flags |= FlagsRegister.Carry;
                    if ((value2 & 0x1F) == 1 && overflow ^ carry)
                        cpu.flags |= FlagsRegister.Overflow;
                    break;

                case OpCodeManager.InstructionType.Sar:
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                    {
                        carry = (((sbyte)value1 >> ((value2 & 0x1F) - 1)) & 1) != 0;
                        result = (sbyte)value1 >> (value2 & 0x1F);
                    }
                    else
                    {
                        carry = (((short)value1 >> ((value2 & 0x1F) - 1)) & 1) != 0;
                        result = (short)value1 >> (value2 & 0x1F);
                    }
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, (ushort)(value2 & 0x1F), result);
                    if (carry)
                        cpu.flags |= FlagsRegister.Carry;
                    break;

                case OpCodeManager.InstructionType.Shr:
                    carry = ((value1 >> ((value2 & 0x1F) - 1)) & 1) != 0;
                    result = value1 >> (value2 & 0x1F);
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, (ushort)(value2 & 0x1F), result);
                    if (carry)
                        cpu.flags |= FlagsRegister.Carry;
                    if ((value2 & 0x1F) == 1)
                    {
                        if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                        {
                            if ((value1 & 0x80) != 0)
                                cpu.flags |= FlagsRegister.Overflow;
                        }
                        else
                        {
                            if ((value1 & 0x8000) != 0)
                                cpu.flags |= FlagsRegister.Overflow;
                        }
                    }
                    break;

                case OpCodeManager.InstructionType.Xor:
                    result = value1 ^ value2;
                    cpu.CalculateBitwiseFlags(instruction.Flag, value1, value2, result);
                    break;

                default:
                    throw new NotImplementedException();
            }

            var truncResult = instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8) ? (byte)result : (ushort)result;

            if (instruction.Type != OpCodeManager.InstructionType.Compare && instruction.Type != OpCodeManager.InstructionType.Test)
            {
                switch (instruction.Argument1)
                {
                    case (int)Register.AX:
                    case (int)Register.CX:
                    case (int)Register.DX:
                    case (int)Register.BX:
                    case (int)Register.SP:
                    case (int)Register.BP:
                    case (int)Register.SI:
                    case (int)Register.DI:
                    case (int)Register.IP:
                    case (int)Register.CS:
                    case (int)Register.DS:
                    case (int)Register.ES:
                    case (int)Register.SS:
                        cpu.registers[instruction.Argument1] = truncResult;
                        break;

                    case OpCodeManager.ARG_BYTE_REGISTER:
                        cpu.SetRegisterU8((Register)instruction.Argument1Value, (byte)truncResult);
                        break;

                    case OpCodeManager.ARG_DEREFERENCE:
                    case OpCodeManager.ARG_MEMORY:
                        var address = cpu.GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
                        if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                            cpu.WriteU8(address, (byte)truncResult);
                        else cpu.WriteU16(address, truncResult);
                        break;

                    default:
                        throw new NotImplementedException();
                }
            }
        }
        private static void DispatchUnaryArithmetic(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var value = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
            int result;

            switch (instruction.Type)
            {
                case OpCodeManager.InstructionType.Decrement:
                    result = value - 1;
                    cpu.CalculateDecFlags(instruction.Flag, value, 1, result);
                    break;
                case OpCodeManager.InstructionType.Increment:
                    result = value + 1;
                    cpu.CalculateIncFlags(instruction.Flag, value, 1, result);
                    break;
                case OpCodeManager.InstructionType.Negate:
                    result = ~value + 1;
                    cpu.CalculateSubFlags(instruction.Flag, 0, value, result);
                    break;
                case OpCodeManager.InstructionType.Not:
                    result = ~value;
                    break;
                default:
                    throw new OutOfMemoryException();
            }

            switch (instruction.Argument1)
            {
                case (int)Register.AX:
                case (int)Register.CX:
                case (int)Register.DX:
                case (int)Register.BX:
                case (int)Register.SP:
                case (int)Register.BP:
                case (int)Register.SI:
                case (int)Register.DI:
                case (int)Register.IP:
                case (int)Register.CS:
                case (int)Register.DS:
                case (int)Register.ES:
                case (int)Register.SS:
                    cpu.registers[instruction.Argument1] = (ushort)result;
                    break;

                case OpCodeManager.ARG_BYTE_REGISTER:
                    cpu.SetRegisterU8((Register)instruction.Argument1Value, (byte)result);
                    break;

                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    var address = cpu.GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
                    if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                        cpu.WriteU8(address, (byte)result);
                    else cpu.WriteU16(address, (ushort)result);
                    break;

                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchLea(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var address = cpu.GetInstructionAddress(instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);

            switch ((Register)instruction.Argument1)
            {
                case Register.AX:
                case Register.CX:
                case Register.DX:
                case Register.BX:
                case Register.SP:
                case Register.BP:
                case Register.SI:
                case Register.DI:
                case Register.IP:
                case Register.CS:
                case Register.DS:
                case Register.ES:
                case Register.SS:
                    cpu.registers[instruction.Argument1] = address;
                    break;

                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchExchange(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            switch (instruction.Argument1)
            {
                case (int)Register.AX:
                case (int)Register.CX:
                case (int)Register.DX:
                case (int)Register.BX:
                case (int)Register.SP:
                case (int)Register.BP:
                case (int)Register.SI:
                case (int)Register.DI:
                case (int)Register.IP:
                case (int)Register.CS:
                case (int)Register.DS:
                case (int)Register.ES:
                case (int)Register.SS:
                    cpu.registers[instruction.Argument1] = cpu.ProcessExchangeSecond(instruction, cpu.registers[instruction.Argument1]);
                    break;

                case OpCodeManager.ARG_BYTE_REGISTER:
                    cpu.SetRegister((Register)instruction.Argument1Value, (byte)cpu.ProcessExchangeSecond(instruction, cpu.GetRegisterU8((Register)instruction.Argument1Value)));
                    break;

                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchFarJump(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            switch (instruction.Argument1)
            {
                case OpCodeManager.ARG_FAR_MEMORY:
                    cpu.SetRegister(Register.CS, (ushort)((uint)instruction.Argument1Value >> 16));
                    cpu.SetRegister(Register.IP, (ushort)(instruction.Argument1Value & 0xFFFF));
                    break;

                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    var address = cpu.GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
                    cpu.SetRegister(Register.CS, cpu.ReadU16(address + 2));
                    cpu.SetRegister(Register.IP, cpu.ReadU16(address));
                    break;

                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchCbw(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetRegister(Register.AX, (ushort)(sbyte)cpu.GetRegisterU8(Register.AL));
        private static void DispatchCwd(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetRegister(Register.DX, (cpu.GetRegister(Register.AX) & 0x8000) != 0 ? (ushort)0xFFFF : (ushort)0);
        private static void DispatchCallNearRelative(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            Debug.Assert(instruction.Argument1 == OpCodeManager.ARG_CONSTANT);
            cpu.Push(cpu.GetRegister(Register.IP));
            cpu.registers[(int)Register.IP] += (ushort)instruction.Argument1Value;
        }
        private static void DispatchCallNear(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var address = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
            cpu.Push(cpu.GetRegister(Register.IP));
            cpu.SetRegister(Register.IP, address);
        }
        private static void DispatchCallFar(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            cpu.Push(cpu.GetRegister(Register.CS));
            cpu.Push(cpu.GetRegister(Register.IP));
            DispatchFarJump(cpu, instruction);
        }
        private static void DispatchWait(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            throw new NotImplementedException();
        }
        private static void DispatchSahf(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            const FlagsRegister flagsAffected = FlagsRegister.Sign | FlagsRegister.Zero | FlagsRegister.Auxiliary | FlagsRegister.Parity | FlagsRegister.Carry;
            cpu.flags &= ~flagsAffected;
            cpu.flags |= (FlagsRegister)(cpu.GetRegister(Register.AH) & (ushort)flagsAffected);
        }
        private static void DispatchLahf(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetRegister(Register.AH, (byte)cpu.GetFlags());
        private static void DispatchAas(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var al = cpu.GetRegisterU8(Register.AL);
            var flags = cpu.GetFlags();

            if ((al & 0xF) > 9 || flags.Has(FlagsRegister.Auxiliary))
            {
                var ah = cpu.GetRegisterU8(Register.AH);

                al = (byte)((al - 6) & 0x0F);
                ah--;

                cpu.SetRegisterU8(Register.AH, ah);
                flags |= FlagsRegister.Carry | FlagsRegister.Auxiliary;
            }
            else
            {
                al &= 0x0F;
                flags &= ~(FlagsRegister.Carry | FlagsRegister.Auxiliary);
            }

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Zero | FlagsRegister.Sign);
            flags |= (parityLookup[al] ? FlagsRegister.Parity : 0) |
                     (al == 0 ? FlagsRegister.Zero : 0) |
                     ((al & 0x80) != 0 ? FlagsRegister.Sign : 0);

            cpu.SetFlags(flags);
            cpu.SetRegisterU8(Register.AL, al);
        }
        private static void DispatchAaa(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var al = cpu.GetRegisterU8(Register.AL);
            var flags = cpu.GetFlags();

            if ((al & 0xF) > 9 || flags.Has(FlagsRegister.Auxiliary))
            {
                var ah = cpu.GetRegisterU8(Register.AH);

                al = (byte)((al + 6) & 0x0F);
                ah++;

                cpu.SetRegisterU8(Register.AH, ah);
                flags |= FlagsRegister.Carry | FlagsRegister.Auxiliary;
            }
            else
            {
                al &= 0x0F;
                flags &= ~(FlagsRegister.Carry | FlagsRegister.Auxiliary);
            }

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Zero | FlagsRegister.Sign);
            flags |= (parityLookup[al] ? FlagsRegister.Parity : 0) |
                     (al == 0 ? FlagsRegister.Zero : 0) |
                     ((al & 0x80) != 0 ? FlagsRegister.Sign : 0);

            cpu.SetFlags(flags);
            cpu.SetRegisterU8(Register.AL, al);
        }
        private static void DispatchDaa(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var al = cpu.GetRegisterU8(Register.AL);
            var oldAl = al;
            var flags = cpu.GetFlags();
            var oldCarry = flags.Has(FlagsRegister.Carry);

            if ((al & 0xF) > 9 || flags.Has(FlagsRegister.Auxiliary))
            {
                al += 6;
                if (oldCarry || (al < oldAl))
                    flags |= FlagsRegister.Carry;
                else flags &= ~FlagsRegister.Carry;
                flags |= FlagsRegister.Auxiliary;
            }
            else flags &= ~FlagsRegister.Auxiliary;

            if (oldAl > 0x99 || oldCarry)
            {
                al += 0x60;
                flags |= FlagsRegister.Carry;
            }
            else flags &= ~FlagsRegister.Carry;

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Zero | FlagsRegister.Sign);
            flags |= (parityLookup[al] ? FlagsRegister.Parity : 0) |
                     (al == 0 ? FlagsRegister.Zero : 0) |
                     ((al & 0x80) != 0 ? FlagsRegister.Sign : 0);

            cpu.SetFlags(flags);
            cpu.SetRegister(Register.AL, al);
        }
        private static void DispatchDas(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var al = cpu.GetRegisterU8(Register.AL);
            var oldAl = al;
            var flags = cpu.GetFlags();
            var oldCarry = flags.Has(FlagsRegister.Carry);
            flags &= ~FlagsRegister.Carry;

            if ((al & 0xF) > 9 || flags.Has(FlagsRegister.Auxiliary))
            {
                al -= 6;
                if (oldCarry || (al > oldAl))
                    flags |= FlagsRegister.Carry;
                else flags &= ~FlagsRegister.Carry;
                flags |= FlagsRegister.Auxiliary;
            }
            else flags &= ~FlagsRegister.Auxiliary;

            if (oldAl > 0x99 || oldCarry)
            {
                al -= 0x60;
                flags |= FlagsRegister.Carry;
            }
            else flags &= ~FlagsRegister.Carry;

            flags &= ~(FlagsRegister.Parity | FlagsRegister.Zero | FlagsRegister.Sign);
            flags |= (parityLookup[al] ? FlagsRegister.Parity : 0) |
                     (al == 0 ? FlagsRegister.Zero : 0) |
                     ((al & 0x80) != 0 ? FlagsRegister.Sign : 0);

            cpu.SetFlags(flags);
            cpu.SetRegister(Register.AL, al);
        }
        private static void DispatchStringOperation(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            ushort counter;
            switch (instruction.OpcodePrefix)
            {
                case 0:
                    DispatchOneStringOperation(cpu, instruction);
                    break;

                case 0xF2:
                    counter = cpu.GetRegister(Register.CX);
                    if (instruction.Type == OpCodeManager.InstructionType.Cmps || instruction.Type == OpCodeManager.InstructionType.Scas)
                    {
                        while (counter != 0)
                        {
                            DispatchOneStringOperation(cpu, instruction);
                            counter--;
                            if (cpu.GetFlags().Has(FlagsRegister.Zero))
                                break;
                        }
                    }
                    else
                    {
                        while (counter != 0)
                        {
                            DispatchOneStringOperation(cpu, instruction);
                            counter--;
                        }
                    }
                    cpu.SetRegister(Register.CX, counter);
                    break;

                case 0xF3:
                    counter = cpu.GetRegister(Register.CX);
                    if (instruction.Type == OpCodeManager.InstructionType.Cmps || instruction.Type == OpCodeManager.InstructionType.Scas)
                    {
                        while (counter != 0)
                        {
                            DispatchOneStringOperation(cpu, instruction);
                            counter--;
                            if (!cpu.GetFlags().Has(FlagsRegister.Zero))
                                break;
                        }
                    }
                    else
                    {
                        while (counter != 0)
                        {
                            DispatchOneStringOperation(cpu, instruction);
                            counter--;
                        }
                    }
                    cpu.SetRegister(Register.CX, counter);
                    break;

                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchOneStringOperation(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            switch (instruction.Type)
            {
                case OpCodeManager.InstructionType.Cmps:
                    DispatchCompareString(cpu, instruction);
                    break;
                case OpCodeManager.InstructionType.Lods:
                    DispatchLoadString(cpu, instruction);
                    break;
                case OpCodeManager.InstructionType.Movs:
                    DispatchMoveString(cpu, instruction);
                    break;
                case OpCodeManager.InstructionType.Stos:
                    DispatchStoreString(cpu, instruction);
                    break;
                case OpCodeManager.InstructionType.Scas:
                    DispatchScanString(cpu, instruction);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchCompareString(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            ushort value1;
            ushort value2;
            byte size;
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
            {
                value1 = cpu.ReadU8(SegmentToAddress(cpu.GetRegister(Register.DS), cpu.GetRegister(Register.SI)));
                value2 = cpu.ReadU8(SegmentToAddress(cpu.GetRegister(Register.ES), cpu.GetRegister(Register.DI)));
                size = 1;
            }
            else
            {
                value1 = cpu.ReadU16(SegmentToAddress(cpu.GetRegister(Register.DS), cpu.GetRegister(Register.SI)));
                value2 = cpu.ReadU16(SegmentToAddress(cpu.GetRegister(Register.ES), cpu.GetRegister(Register.DI)));
                size = 2;
            }
            var result = value1 - value2;

            cpu.CalculateSubFlags(instruction.Flag, value1, value2, result);

            if (!cpu.GetFlags().Has(FlagsRegister.Direction))
            {
                cpu.registers[(int)Register.DI] += size;
                cpu.registers[(int)Register.SI] += size;
            }
            else
            {
                cpu.registers[(int)Register.DI] -= size;
                cpu.registers[(int)Register.SI] -= size;
            }
        }
        private static void DispatchLoadString(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var prefix = instruction.SegmentPrefix;
            if (prefix == Register.Invalid) prefix = Register.DS;
            var sourceAddress = SegmentToAddress(cpu.GetRegister(prefix), cpu.GetRegister(Register.SI));

            byte size;
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
            {
                cpu.SetRegisterU8(Register.AL, cpu.ReadU8(sourceAddress));
                size = 1;
            }
            else
            {
                cpu.SetRegister(Register.AX, cpu.ReadU16(sourceAddress));
                size = 2;
            }

            if (!cpu.GetFlags().Has(FlagsRegister.Direction))
                cpu.registers[(int)Register.SI] += size;
            else cpu.registers[(int)Register.SI] -= size;
        }
        private static void DispatchMoveString(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var sourceAddress = SegmentToAddress(cpu.GetRegister(Register.DS), cpu.GetRegister(Register.SI));
            var destAddress = SegmentToAddress(cpu.GetRegister(Register.ES), cpu.GetRegister(Register.DI));
            byte size;
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
            {
                var value = cpu.ReadU8(sourceAddress);
                cpu.WriteU8(destAddress, value);
                size = 1;
            }
            else
            {
                var value = cpu.ReadU16(sourceAddress);
                cpu.WriteU16(destAddress, value);
                size = 2;
            }

            if (!cpu.GetFlags().Has(FlagsRegister.Direction))
            {
                cpu.registers[(int)Register.DI] += size;
                cpu.registers[(int)Register.SI] += size;
            }
            else
            {
                cpu.registers[(int)Register.DI] -= size;
                cpu.registers[(int)Register.SI] -= size;
            }
        }
        private static void DispatchStoreString(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var destAddress = SegmentToAddress(cpu.GetRegister(Register.ES), cpu.GetRegister(Register.DI));
            byte size;
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
            {
                cpu.WriteU8(destAddress, cpu.GetRegisterU8(Register.AX));
                size = 1;
            }
            else
            {
                cpu.WriteU16(destAddress, cpu.GetRegister(Register.AX));
                size = 2;
            }

            if (!cpu.GetFlags().Has(FlagsRegister.Direction))
                cpu.registers[(int)Register.DI] += size;
            else cpu.registers[(int)Register.DI] -= size;
        }
        private static void DispatchScanString(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var sourceAddress = SegmentToAddress(cpu.GetRegister(Register.ES), cpu.GetRegister(Register.DI));
            ushort value1;
            ushort value2;
            byte size;
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
            {
                value1 = cpu.GetRegisterU8(Register.AL);
                value2 = cpu.ReadU8(sourceAddress);
                size = 1;
            }
            else
            {
                value1 = cpu.GetRegister(Register.AX);
                value2 = cpu.ReadU16(sourceAddress);
                size = 2;
            }
            var result = value1 - value2;

            cpu.CalculateSubFlags(instruction.Flag, value1, value2, result);

            if (!cpu.GetFlags().Has(FlagsRegister.Direction))
                cpu.registers[(int)Register.DI] += size;
            else cpu.registers[(int)Register.DI] -= size;
        }
        private static void DispatchReturnNear(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            cpu.SetRegister(Register.IP, cpu.Pop());
            if (instruction.Argument1 == OpCodeManager.ARG_CONSTANT)
                cpu.SetRegister(Register.SP, (ushort)(cpu.GetRegister(Register.SP) + instruction.Argument1Value));
            else Debug.Assert(instruction.Argument1 == OpCodeManager.ARG_NONE);
        }
        private static void DispatchReturnFar(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            cpu.SetRegister(Register.IP, cpu.Pop());
            cpu.SetRegister(Register.CS, cpu.Pop());
            if (instruction.Argument1 == OpCodeManager.ARG_CONSTANT)
                cpu.SetRegister(Register.SP, (ushort)(cpu.GetRegister(Register.SP) + instruction.Argument1Value));
            else Debug.Assert(instruction.Argument1 == OpCodeManager.ARG_NONE);
        }
        private static void DispatchLoadFarPointer(Cpu8086 cpu, OpCodeManager.Instruction instruction, Register register)
        {
            var address = cpu.GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);
            var memory = cpu.ReadU16(address);
            var segment = cpu.ReadU16(address + 2);

            cpu.SetRegister(register, segment);
            switch ((Register)instruction.Argument1)
            {
                case Register.AX:
                case Register.CX:
                case Register.DX:
                case Register.BX:
                case Register.SP:
                case Register.BP:
                case Register.SI:
                case Register.DI:
                case Register.IP:
                case Register.CS:
                case Register.DS:
                case Register.ES:
                case Register.SS:
                    cpu.registers[instruction.Argument1] = memory;
                    break;

                default:
                    throw new NotImplementedException();
            }
        }
        private static void DispatchInterrupt(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            Debug.Assert(instruction.Argument1 == OpCodeManager.ARG_CONSTANT);
            cpu.Interrupt((byte)instruction.Argument1Value);
        }
        private static void DispatchInto(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            throw new NotImplementedException();
        }
        private static void DispatchReturnInterrupt(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            cpu.SetRegister(Register.IP, cpu.Pop());
            cpu.SetRegister(Register.CS, cpu.Pop());
            cpu.SetRegister(Register.FLAGS, cpu.Pop());
        }
        private static void DispatchAam(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            throw new NotImplementedException();
        }
        private static void DispatchAad(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            throw new NotImplementedException();
        }
        private static void DispatchXlat(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var address = SegmentToAddress(cpu.GetRegister(Register.DS), (ushort)(cpu.GetRegister(Register.BX) + cpu.GetRegisterU8(Register.AL)));
            cpu.SetRegisterU8(Register.AL, cpu.ReadU8(address));
        }
        private static void DispatchLoop(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var counter = --cpu.registers[(int)Register.CX];
            if (counter != 0)
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchLoopZero(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var counter = --cpu.registers[(int)Register.CX];
            if (counter != 0 && cpu.GetFlags().Has(FlagsRegister.Zero))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchLoopNotZero(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var counter = --cpu.registers[(int)Register.CX];
            if (counter != 0 && !cpu.GetFlags().Has(FlagsRegister.Zero))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchIn(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            Debug.Assert(instruction.Argument1 == (int)Register.AX || instruction.Argument1 == OpCodeManager.ARG_BYTE_REGISTER);
            var register = instruction.Argument1 == (int)Register.AX ? Register.AX : (Register)instruction.Argument1Value;
            var port = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                cpu.SetRegisterU8(register, cpu.ReadU8((uint)(IO_PORT_OFFSET + port)));
            else cpu.SetRegister(register, cpu.ReadU16((uint)(IO_PORT_OFFSET + port)));
        }
        private static void DispatchOut(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var port = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
            var value = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                cpu.WriteU8((uint)(IO_PORT_OFFSET + port), (byte)value);
            else cpu.WriteU16((uint)(IO_PORT_OFFSET + port), value);
        }

        private static void DispatchHalt(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            // Goes back one instruction so it halts again if process instruction is called
            --cpu.registers[(int)Register.IP];

        private static void DispatchCmc(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetFlags(cpu.GetFlags() ^ FlagsRegister.Carry);

        private static void DispatchClc(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetFlags(cpu.GetFlags() & ~FlagsRegister.Carry);
        private static void DispatchCld(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetFlags(cpu.GetFlags() & ~FlagsRegister.Direction);
        private static void DispatchCli(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetFlags(cpu.GetFlags() & ~FlagsRegister.Interrupt);

        private static void DispatchStc(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetFlags(cpu.GetFlags() | FlagsRegister.Carry);
        private static void DispatchStd(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetFlags(cpu.GetFlags() | FlagsRegister.Direction);
        private static void DispatchSti(Cpu8086 cpu, OpCodeManager.Instruction instruction) =>
            cpu.SetFlags(cpu.GetFlags() | FlagsRegister.Interrupt);

        private static void DispatchJumpRelative(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            Debug.Assert(instruction.Argument1 == OpCodeManager.ARG_CONSTANT);
            cpu.registers[(int)Register.IP] += (ushort)instruction.Argument1Value;
        }
        private static void DispatchJump(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var value = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
            cpu.SetRegister(Register.IP, value);
        }
        private static void DispatchJumpIfOverflow(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (cpu.GetFlags().Has(FlagsRegister.Overflow))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJumpIfNotOverflow(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (!cpu.GetFlags().Has(FlagsRegister.Overflow))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJumpIfCarry(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (cpu.GetFlags().Has(FlagsRegister.Carry))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJumpIfNotCarry(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (!cpu.GetFlags().Has(FlagsRegister.Carry))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJumpIfZero(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (cpu.GetFlags().Has(FlagsRegister.Zero))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJumpIfNotZero(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (!cpu.GetFlags().Has(FlagsRegister.Zero))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJBE(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var flags = cpu.GetFlags();
            if (flags.Has(FlagsRegister.Carry) || flags.Has(FlagsRegister.Zero))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJA(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var flags = cpu.GetFlags();
            if (!flags.Has(FlagsRegister.Carry) && !flags.Has(FlagsRegister.Zero))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJS(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (cpu.GetFlags().Has(FlagsRegister.Sign))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJNS(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (!cpu.GetFlags().Has(FlagsRegister.Sign))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJPE(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (cpu.GetFlags().Has(FlagsRegister.Parity))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJLE(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var flags = cpu.GetFlags();
            if (flags.Has(FlagsRegister.Zero) || flags.Has(FlagsRegister.Sign) != flags.Has(FlagsRegister.Overflow))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJPO(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (!cpu.GetFlags().Has(FlagsRegister.Parity))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJL(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var flags = cpu.GetFlags();
            if (flags.Has(FlagsRegister.Sign) != flags.Has(FlagsRegister.Overflow))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJGE(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var flags = cpu.GetFlags();
            if (flags.Has(FlagsRegister.Sign) == flags.Has(FlagsRegister.Overflow))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJG(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var flags = cpu.GetFlags();
            if (!flags.Has(FlagsRegister.Zero) && flags.Has(FlagsRegister.Sign) == flags.Has(FlagsRegister.Overflow))
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchJcxz(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (cpu.GetRegister(Register.CX) == 0)
                DispatchJumpRelative(cpu, instruction);
        }
        private static void DispatchMultiply(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var value1 = cpu.GetRegister(Register.AX);
            var value2 = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);

            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
            {
                value1 &= 0xFF;
                value2 &= 0xFF;
            }

            uint result;
            switch (instruction.Type)
            {
                case OpCodeManager.InstructionType.Multiply:
                    result = (uint)value1 * value2;
                    break;
                case OpCodeManager.InstructionType.SignedMultiply:
                    result = (uint)(value1 * value2);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            cpu.CalculateAddFlags(instruction.Flag, value1, value2, (int)result);

            cpu.SetRegister(Register.AX, (ushort)result);
            if (!instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                cpu.SetRegister(Register.DX, (ushort)(result >> 16));
        }
        private static void DispatchDivide(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                DispatchDivide8(cpu, instruction);
            else DispatchDivide16(cpu, instruction);
        }
        private static void DispatchDivide8(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            uint value1 = cpu.GetRegister(Register.AX);
            uint value2 = (byte)cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);

            if (value2 == 0)
            {
                cpu.Interrupt(0);
                return;
            }

            var quotient = value1 / value2;
            if (quotient > 0xFF)
            {
                cpu.Interrupt(0);
                return;
            }

            var remainder = value1 % value2;
            cpu.SetRegisterU8(Register.AL, (byte)quotient);
            cpu.SetRegisterU8(Register.AH, (byte)remainder);
        }
        private static void DispatchDivide16(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var value1 = ((uint)cpu.GetRegister(Register.DX) << 16) | cpu.GetRegister(Register.AX);
            uint value2 = cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);

            if (value2 == 0)
            {
                cpu.Interrupt(0);
                return;
            }

            var quotient = value1 / value2;
            if (quotient > 0xFFFF)
            {
                cpu.Interrupt(0);
                return;
            }

            var remainder = value1 % value2;
            cpu.SetRegister(Register.AX, (ushort)quotient);
            cpu.SetRegister(Register.DX, (ushort)remainder);
        }
        private static void DispatchSignedDivide(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            if (instruction.Flag.Has(OpCodeManager.OpCodeFlag.Size8))
                DispatchSignedDivide8(cpu, instruction);
            else DispatchSignedDivide16(cpu, instruction);
        }
        private static void DispatchSignedDivide8(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            int value1 = (short)cpu.GetRegister(Register.AX);
            int value2 = (sbyte)cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);

            if ((uint)value1 == 0x8000 || value2 == 0)
            {
                cpu.Interrupt(0);
                return;
            }

            var quotient = value1 / value2;
            var remainder = value1 % value2;

            if ((quotient & 0xFF) != quotient)
            {
                cpu.Interrupt(0);
                return;
            }

            cpu.SetRegisterU8(Register.AL, (byte)quotient);
            cpu.SetRegisterU8(Register.AH, (byte)remainder);
        }
        private static void DispatchSignedDivide16(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            var value1 = (int)(((uint)cpu.GetRegister(Register.DX) << 16) | cpu.GetRegister(Register.AX));
            int value2 = (short)cpu.GetInstructionValue(instruction.Flag, instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);

            if ((uint)value1 == 0x80000000 || value2 == 0)
            {
                cpu.Interrupt(0);
                return;
            }

            var quotient = value1 / value2;
            var remainder = value1 % value2;

            if ((quotient & 0xFFFF) != quotient)
            {
                cpu.Interrupt(0);
                return;
            }

            cpu.SetRegister(Register.AX, (ushort)quotient);
            cpu.SetRegister(Register.DX, (ushort)remainder);
        }
        private static void DispatchPush(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            switch (instruction.Argument1)
            {
                case (int)Register.SP:
                    // 8086 has a bug where it pushes SP after it has been modified
                    // cpu.registers[(int)Register.SP] -= 2;
                    // cpu.WriteU16(SegmentToAddress(cpu.GetRegister(Register.SS), cpu.GetRegister(Register.SP)), cpu.GetRegister(Register.SP));
                    // break;
                case (int)Register.AX:
                case (int)Register.CX:
                case (int)Register.DX:
                case (int)Register.BX:
                case (int)Register.BP:
                case (int)Register.SI:
                case (int)Register.DI:
                case (int)Register.IP:
                case (int)Register.CS:
                case (int)Register.DS:
                case (int)Register.ES:
                case (int)Register.SS:
                case unchecked((int)Register.FLAGS):
                    cpu.Push(cpu.GetRegister((Register)instruction.Argument1));
                    break;

                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    var address = cpu.GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
                    cpu.Push(cpu.ReadU16(address));
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        private static void DispatchPop(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            switch (instruction.Argument1)
            {
                case (int)Register.AX:
                case (int)Register.CX:
                case (int)Register.DX:
                case (int)Register.BX:
                case (int)Register.SP:
                case (int)Register.BP:
                case (int)Register.SI:
                case (int)Register.DI:
                case (int)Register.IP:
                case (int)Register.CS:
                case (int)Register.DS:
                case (int)Register.ES:
                case (int)Register.SS:
                //case unchecked((int)Register.FLAGS):
                    cpu.SetRegister((Register)instruction.Argument1, cpu.Pop());
                    break;
                case unchecked((int)Register.FLAGS):
                    cpu.SetRegister((Register)instruction.Argument1, cpu.Pop());
                    break;

                case OpCodeManager.ARG_DEREFERENCE:
                case OpCodeManager.ARG_MEMORY:
                    var address = cpu.GetInstructionRealAddress(instruction.SegmentPrefix, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
                    cpu.WriteU16(address, cpu.Pop());
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void DispatchEmulatorSpecial(Cpu8086 cpu, OpCodeManager.Instruction instruction)
        {
            Debug.Assert(instruction.Argument1 == OpCodeManager.ARG_CONSTANT);
            switch (instruction.Argument1Value)
            {
                case 0x01:
                    DispatchSetupBios(cpu);
                    break;
                case 0x02:
                    DispatchDisk(cpu);
                    break;
                case 0x03:
                    DispatchClock(cpu);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        private static unsafe void DispatchSetupBios(Cpu8086 cpu)
        {
            var destination = cpu.GetRegister(Register.AX);
            var address = SegmentToAddress(cpu.GetRegister(Register.CS), destination);
            var biosData = cpu.CreateBiosData();
            var biosDataPtr = (byte*)&biosData;
            for (var i = 0u; i < 0x100; i++)
                cpu.WriteU8(address + i, biosDataPtr[i]);
        }

        private static void DispatchDisk(Cpu8086 cpu)
        {
            switch (cpu.GetRegisterU8(Register.AH))
            {
                case 0x02:
                    DispatchReadDrive(cpu);
                    break;
                case 0x08:
                    DispatchDriveInformation(cpu);
                    break;
                case 0x15:
                    DispatchDiskInformation(cpu);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        private static void DispatchReadDrive(Cpu8086 cpu)
        {
            var driveNumber = cpu.ReadU8(SegmentToAddress(cpu.GetRegister(Register.SS), (ushort)(cpu.GetRegister(Register.BP) - 1)));
            var headNumber = cpu.ReadU8(SegmentToAddress(cpu.GetRegister(Register.SS), (ushort)(cpu.GetRegister(Register.BP) - 2)));
            var cylinderNumber = cpu.ReadU16(SegmentToAddress(cpu.GetRegister(Register.SS), (ushort)(cpu.GetRegister(Register.BP) - 4)));
            var sectorNumber = cpu.ReadU8(SegmentToAddress(cpu.GetRegister(Register.SS), (ushort)(cpu.GetRegister(Register.BP) - 5)));
            var sectorsToRead = cpu.ReadU8(SegmentToAddress(cpu.GetRegister(Register.SS), (ushort)(cpu.GetRegister(Register.BP) - 6)));
            var destinationSegment = cpu.ReadU16(SegmentToAddress(cpu.GetRegister(Register.SS), (ushort)(cpu.GetRegister(Register.BP) - 8)));
            var destinationOffset = cpu.ReadU16(SegmentToAddress(cpu.GetRegister(Register.SS), (ushort)(cpu.GetRegister(Register.BP) - 10)));

            var destination = SegmentToAddress(destinationSegment, destinationOffset);

            if (cpu.drives[driveNumber] != null)
            {
                var drive = cpu.drives[driveNumber];
                var lba = (cylinderNumber * (uint)drive.NumberHeads + headNumber) * drive.NumberSectors + sectorNumber - 1;
                var driveOffset = lba * 512;
                cpu.WriteBytes(destination, drive.Read(driveOffset, sectorsToRead * 512u));
                cpu.SetRegister(Register.AX, 0);
            }
            else cpu.SetRegister(Register.AX, 0x0C);
        }
        private static void DispatchDriveInformation(Cpu8086 cpu)
        {
            var driveTable = cpu.GetRegister(Register.AX);
            var driveNumber = cpu.GetRegisterU8(Register.DL);

            if (cpu.drives[driveNumber] != null)
            {
                var drive = cpu.drives[driveNumber];

                cpu.SetRegister(Register.AX, 0);
                cpu.SetRegister(Register.BL, drive.FloppyType);
                cpu.SetRegisterU8(Register.CH, (byte)((drive.NumberCylinders - 1) & 0xFF));
                cpu.SetRegisterU8(Register.CL, (byte)((((drive.NumberCylinders - 1) >> 2) & 0xC0) | (drive.NumberSectors & 0x3F)));
                cpu.SetRegisterU8(Register.DH, (byte)(drive.NumberHeads - 1));

                byte numberDrives;
                if ((driveNumber & 0x80) != 0)
                {
                    for (numberDrives = 0; numberDrives < 0x80; numberDrives++)
                        if (cpu.drives[0x80 + numberDrives] == null)
                            break;
                }
                else
                {
                    for (numberDrives = 0; numberDrives < 0x80; numberDrives++)
                        if (cpu.drives[numberDrives] == null)
                            break;

                    var address = (ushort)(driveTable + 0x10 * driveNumber);

                    cpu.WriteU8(SegmentToAddress(0xF000, address), (byte)drive.NumberCylinders);
                    cpu.WriteU8(SegmentToAddress(0xF000, address) + 1, drive.NumberHeads);
                    cpu.WriteU8(SegmentToAddress(0xF000, address) + 0xE, drive.NumberSectors);

                    cpu.SetRegister(Register.ES, 0xF000);
                    cpu.SetRegister(Register.DI, address);
                }
                cpu.SetRegisterU8(Register.DL, numberDrives);
            }
            else cpu.SetRegister(Register.AH, 0x07);
        }
        private static void DispatchDiskInformation(Cpu8086 cpu)
        {
            var driveNumber = cpu.GetRegisterU8(Register.DL);

            if (cpu.drives[driveNumber] != null)
            {
                var drive = cpu.drives[driveNumber];

                if (drive.FloppyType == 0)
                {
                    var size = (uint)drive.NumberHeads * drive.NumberCylinders * drive.NumberSectors;
                    cpu.SetRegister(Register.AH, 0x03);
                    cpu.SetRegister(Register.DX, (ushort)(size & 0xFFFF));
                    cpu.SetRegister(Register.CX, (ushort)(size >> 16));
                }
                else cpu.SetRegister(Register.AH, 0x01);
            }
            else cpu.SetRegister(Register.AH, 0x00);
        }

        private static void DispatchClock(Cpu8086 cpu)
        {
            switch (cpu.GetRegisterU8(Register.AH))
            {
                case 0x00:
                    DispatchGetClock(cpu);
                    break;
                case 0x02:
                    DispatchGetRTC(cpu);
                    break;
                case 0x04:
                    DispatchGetDate(cpu);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        private static void DispatchGetClock(Cpu8086 cpu)
        {
            var date = DateTime.Now;
            var time = date - DateTime.Today;
            var timePercentage = (double)TimeSpan.TicksPerDay / time.Ticks;
            var biosTicks = (uint)(timePercentage * 0x1800B0);

            cpu.SetRegister(Register.DX, (ushort)biosTicks);
            cpu.SetRegister(Register.CX, (ushort)(biosTicks >> 16));
            cpu.SetRegisterU8(Register.AH, (byte)(date.DayOfYear != cpu.lastBiosTime.DayOfYear ? 1 : 0));
            cpu.lastBiosTime = date;
        }
        private static void DispatchGetRTC(Cpu8086 cpu)
        {
            var time = DateTime.Now;
            cpu.SetRegisterU8(Register.CH, (byte)time.Hour);
            cpu.SetRegisterU8(Register.CL, (byte)time.Minute);
            cpu.SetRegisterU8(Register.DH, (byte)time.Second);
            cpu.SetRegisterU8(Register.DL, (byte)(time.IsDaylightSavingTime() ? 1 : 0));
        }
        private static void DispatchGetDate(Cpu8086 cpu)
        {
            var time = DateTime.Now;
            cpu.SetRegisterU8(Register.CH, (byte)(time.Year / 100));
            cpu.SetRegisterU8(Register.CL, (byte)(time.Year % 100));
            cpu.SetRegisterU8(Register.DH, (byte)time.Month);
            cpu.SetRegisterU8(Register.DL, (byte)time.Day);
        }

        private static string OutputInstruction(OpCodeManager.Instruction instruction)
        {
            var output = instruction.Type.ToString();
            var arg1 = OutputArgument(instruction.SegmentPrefix, instruction.Flag, instruction.Argument1, instruction.Argument1Value, instruction.Argument1Displacement);
            var arg2 = OutputArgument(instruction.SegmentPrefix, instruction.Flag, instruction.Argument2, instruction.Argument2Value, instruction.Argument2Displacement);

            if (arg1 == null)
                return output;
            return arg2 == null ? $"{output} {arg1}" : $"{output} {arg1}, {arg2}";
        }
        private static string OutputArgument(Register segmentPrefix, OpCodeManager.OpCodeFlag flag, int argument, int argumentValue, int argumentDisplacement)
        {
            if (argument == OpCodeManager.ARG_NONE)
                return null;
            switch (argument)
            {
                case (int)Register.AX:
                    return "AX";
                case (int)Register.CX:
                    return "CX";
                case (int)Register.DX:
                    return "DX";
                case (int)Register.BX:
                    return "BX";
                case (int)Register.SP:
                    return "SP";
                case (int)Register.BP:
                    return "BP";
                case (int)Register.SI:
                    return "SI";
                case (int)Register.DI:
                    return "DI";
                case (int)Register.IP:
                    return "IP";
                case (int)Register.CS:
                    return "CS";
                case (int)Register.DS:
                    return "DS";
                case (int)Register.ES:
                    return "ES";
                case (int)Register.SS:
                    return "SS";
                case unchecked((int)Register.FLAGS):
                    return "FLAGS";

                case OpCodeManager.ARG_BYTE_REGISTER:
                    switch ((Register)argumentValue)
                    {
                        case Register.AL:
                            return "AL";
                        case Register.CL:
                            return "CL";
                        case Register.DL:
                            return "DL";
                        case Register.BL:
                            return "BL";
                        case Register.AH:
                            return "AH";
                        case Register.CH:
                            return "CH";
                        case Register.DH:
                            return "DH";
                        case Register.BH:
                            return "BH";
                        default:
                            throw new NotImplementedException();
                    }
                case OpCodeManager.ARG_DEREFERENCE:
                    string value;
                    switch (argumentValue)
                    {
                        case 0:
                            value = "BX+SI";
                            break;
                        case 1:
                            value = "BX+DI";
                            break;
                        case 2:
                            value = "BP+SI";
                            break;
                        case 3:
                            value = "BP+DI";
                            break;
                        case 4:
                            value = "SI";
                            break;
                        case 5:
                            value = "DI";
                            break;
                        case 6:
                            value = "BP";
                            break;
                        case 7:
                            value = "BX";
                            break;
                        default:
                            throw new NotImplementedException();
                    }
                    switch (segmentPrefix)
                    {
                        case Register.Invalid:
                            return argumentDisplacement < 0 ? $"[{value}{argumentDisplacement}]" : $"[{value}+{argumentDisplacement}]";
                        case Register.ES:
                            return argumentDisplacement < 0 ? $"[ES:{value}{argumentDisplacement}]" : $"[ES:{value}+{argumentDisplacement}]";
                        case Register.CS:
                            return argumentDisplacement < 0 ? $"[CS:{value}{argumentDisplacement}]" : $"[CS:{value}+{argumentDisplacement}]";
                        case Register.SS:
                            return argumentDisplacement < 0 ? $"[SS:{value}{argumentDisplacement}]" : $"[SS:{value}+{argumentDisplacement}]";
                        case Register.DS:
                            return argumentDisplacement < 0 ? $"[DS:{value}{argumentDisplacement}]" : $"[DS:{value}+{argumentDisplacement}]";
                        default:
                            throw new NotImplementedException();
                    }
                case OpCodeManager.ARG_MEMORY:
                    switch (segmentPrefix)
                    {
                        case Register.Invalid:
                            return $"[{argumentValue:X4}]";
                        case Register.ES:
                            return $"[ES:{argumentValue:X4}]";
                        case Register.CS:
                            return $"[CS:{argumentValue:X4}]";
                        case Register.SS:
                            return $"[SS:{argumentValue:X4}]";
                        case Register.DS:
                            return $"[DS:{argumentValue:X4}]";
                        default:
                            throw new NotImplementedException();
                    }
                case OpCodeManager.ARG_FAR_MEMORY:
                    var segment = (uint)argumentValue >> 16;
                    var address = argumentValue & 0xFFFF;
                    return $"[{segment:X4}:{address:X4}]";
                case OpCodeManager.ARG_CONSTANT:
                    return flag.Has(OpCodeManager.OpCodeFlag.Size8) ? $"{argumentValue:X2}" : $"{argumentValue:X4}";
                default:
                    throw new NotImplementedException();
            }
        }

        private static uint SegmentToAddress(ushort segment, ushort offset) => (uint)((segment << 4) + offset);

        public ushort GetRegister(Register register)
        {
            if (((uint)register & 0x80000000) == 0)
                return registers[(int)register];
            switch (register)
            {
                case Register.AL:
                    return (byte)(GetRegister(Register.AX) & 0xFF);
                case Register.CL:
                    return (byte)(GetRegister(Register.CX) & 0xFF);
                case Register.DL:
                    return (byte)(GetRegister(Register.DX) & 0xFF);
                case Register.BL:
                    return (byte)(GetRegister(Register.BX) & 0xFF);
                case Register.AH:
                    return (byte)((GetRegister(Register.AX) >> 8) & 0xFF);
                case Register.CH:
                    return (byte)((GetRegister(Register.CX) >> 8) & 0xFF);
                case Register.DH:
                    return (byte)((GetRegister(Register.DX) >> 8) & 0xFF);
                case Register.BH:
                    return (byte)((GetRegister(Register.BX) >> 8) & 0xFF);

                case Register.FLAGS:
                    return (ushort)((ushort)flags | 2);

                default:
                    throw new ArgumentOutOfRangeException(nameof(register));
            }
        }
        public byte GetRegisterU8(Register register) => (byte)GetRegister(register);
        public FlagsRegister GetFlags() => (FlagsRegister)GetRegister(Register.FLAGS);

        public void SetRegister(Register register, ushort value)
        {
            if (((uint)register & 0x80000000) == 0)
                registers[(int)register] = value;
            else
            {
                switch (register)
                {
                    case Register.AL:
                        registers[(int)Register.AX] = (ushort)((GetRegister(Register.AX) & 0xFF00) | value);
                        break;
                    case Register.CL:
                        registers[(int)Register.CX] = (ushort)((GetRegister(Register.CX) & 0xFF00) | value);
                        break;
                    case Register.DL:
                        registers[(int)Register.DX] = (ushort)((GetRegister(Register.DX) & 0xFF00) | value);
                        break;
                    case Register.BL:
                        registers[(int)Register.BX] = (ushort)((GetRegister(Register.BX) & 0xFF00) | value);
                        break;
                    case Register.AH:
                        registers[(int)Register.AX] = (ushort)((GetRegister(Register.AX) & 0x00FF) | (value << 8));
                        break;
                    case Register.CH:
                        registers[(int)Register.CX] = (ushort)((GetRegister(Register.CX) & 0x00FF) | (value << 8));
                        break;
                    case Register.DH:
                        registers[(int)Register.DX] = (ushort)((GetRegister(Register.DX) & 0x00FF) | (value << 8));
                        break;
                    case Register.BH:
                        registers[(int)Register.BX] = (ushort)((GetRegister(Register.BX) & 0x00FF) | (value << 8));
                        break;

                    case Register.FLAGS:
                        flags = (FlagsRegister)value & FLAGS_MASK;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(register));
                }
            }
        }
        public void SetRegisterU8(Register register, byte value) => SetRegister(register, value);
        public void SetFlags(FlagsRegister value) => SetRegister(Register.FLAGS, (ushort)value);

        public byte ReadU8(uint address)
        {
            if (address == 0x410)
                return 0x41;
            var pageNumber = address >> PAGE_SHIFT;
            return pages[pageNumber].ReadU8(address);
        }
        public ushort ReadU16(uint address)
        {
            //if (address == 0x03E4)
            //    Debugger.Break();
            return (ushort)(ReadU8(address) | ReadU8(address + 1) << 8);
        }
        public byte[] ReadBytes(uint address, uint size)
        {
            var buffer = new byte[size];
            for (var i = 0u; i < size; i++)
                buffer[i] = ReadU8(address + i);
            return buffer;
        }
        public void WriteU8(uint address, byte value)
        {
            var pageNumber = address >> PAGE_SHIFT;
            pages[pageNumber].WriteU8(address, value);
        }
        public void WriteU16(uint address, ushort value)
        {
            WriteU8(address, (byte)(value & 0xFF));
            WriteU8(address + 1, (byte)(value >> 8 & 0xFF));
        }
        public void WriteBytes(uint address, byte[] value)
        {
            for (var i = 0u; i < value.Length; i++)
                WriteU8(address + i, value[i]);
        }

        byte IInstructionFetcher.FetchU8()
        {
            var value = ReadU8(SegmentToAddress(GetRegister(Register.CS), GetRegister(Register.IP)));
            registers[(int)Register.IP] += 1;
            return value;
        }
        ushort IInstructionFetcher.FetchU16()
        {
            var value = ReadU16(SegmentToAddress(GetRegister(Register.CS), GetRegister(Register.IP)));
            registers[(int)Register.IP] += 2;
            return value;
        }
    }
}
