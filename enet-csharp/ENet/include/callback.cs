// ReSharper disable ALL

namespace enet
{
    public unsafe struct ENetCallbacks
    {
        public delegate* managed<nint, void*> malloc;
        public delegate* managed<void*, void> free;

        public ENetCallbacks(delegate*<nint, void*> malloc, delegate*<void*, void> free)
        {
            this.malloc = malloc;
            this.free = free;
        }
    }
}