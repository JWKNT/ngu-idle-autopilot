using System.IO;

/*
FILE PURPOSE

AllocationProfile is the abstract contract between Main's schedulers and concrete allocation
policy. Implementations allocate Energy, Magic, R3, gear, and diggers through native systems.
Callers rely on each pass being repeatable and state-derived; lifecycle and optimization policy
belong in concrete profiles, not this interface.
*/
namespace NGUInjector.AllocationProfiles
{
    internal abstract class AllocationProfile
    {
        protected Character _character;
        protected EnergyInputController _energyController;
        protected StreamWriter _outputWriter;

        protected AllocationProfile()
        {
            _character = Main.Character;
            _energyController = _character.energyMagicPanel;
            _outputWriter = Main.OutputWriter;
        }

        public abstract void AllocateEnergy();
        public abstract void AllocateMagic();
        public abstract void AllocateR3();
        public abstract void EquipGear();
        public abstract void EquipDiggers();
    }
}
