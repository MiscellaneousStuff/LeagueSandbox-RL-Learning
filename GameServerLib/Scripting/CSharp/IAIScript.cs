using GameServerCore.Domain;
using GameServerCore.Domain.GameObjects;

namespace LeagueSandbox.GameServer.Scripting.CSharp
{
    public interface IAIScript
    {
        void Update(double diff);
    }
}