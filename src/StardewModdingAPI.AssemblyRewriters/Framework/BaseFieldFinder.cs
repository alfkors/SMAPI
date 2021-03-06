using Mono.Cecil;
using Mono.Cecil.Cil;

namespace StardewModdingAPI.AssemblyRewriters.Framework
{
    /// <summary>Base class for a field finder.</summary>
    public abstract class BaseFieldFinder : IInstructionFinder
    {
        /*********
        ** Accessors
        *********/
        /// <summary>A brief noun phrase indicating what the instruction finder matches.</summary>
        public abstract string NounPhrase { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Get whether a CIL instruction matches.</summary>
        /// <param name="instruction">The IL instruction.</param>
        /// <param name="platformChanged">Whether the mod was compiled on a different platform.</param>
        public bool IsMatch(Instruction instruction, bool platformChanged)
        {
            if (instruction.OpCode != OpCodes.Ldfld && instruction.OpCode != OpCodes.Ldsfld && instruction.OpCode != OpCodes.Stfld && instruction.OpCode != OpCodes.Stsfld)
                return false; // not a field reference
            return this.IsMatch(instruction, (FieldReference)instruction.Operand, platformChanged);
        }


        /*********
        ** Protected methods
        *********/
        /// <summary>Get whether a field reference should be rewritten.</summary>
        /// <param name="instruction">The IL instruction.</param>
        /// <param name="fieldRef">The field reference.</param>
        /// <param name="platformChanged">Whether the mod was compiled on a different platform.</param>
        protected abstract bool IsMatch(Instruction instruction, FieldReference fieldRef, bool platformChanged);

        /// <summary>Whether an instruction is a static field reference.</summary>
        /// <param name="instruction">The IL instruction.</param>
        protected bool IsStaticField(Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Ldsfld || instruction.OpCode == OpCodes.Stsfld;
        }
    }
}
