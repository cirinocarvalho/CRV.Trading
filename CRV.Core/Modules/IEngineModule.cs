using CRV.Core.Models;

namespace CRV.Core.Modules;

public interface IEngineModule
{
    void OnBar(Bar bar, DateTime tradingDate);
    void OnTick(decimal price, DateTime utcTime);
    void NewSession(DateTime tradingDate);
}
