using NUnit.Framework;
using CitySim.Simulation;

namespace Tests.Simulation
{
    [TestFixture]
    public class EconomyManagerTests
    {
        [Test]
        public void InitialBalance_ShouldBe50000()
        {
            var economy = new EconomyManager();
            Assert.That(economy.Money, Is.EqualTo(50000f));
        }

        [Test]
        public void Spend_SufficientFunds_ReturnsTrueAndDeducts()
        {
            var economy = new EconomyManager();
            bool result = economy.Spend(100f);
            Assert.That(result, Is.True);
            Assert.That(economy.Money, Is.EqualTo(49900f));
        }

        [Test]
        public void Spend_InsufficientFunds_ReturnsFalseAndNoDeduction()
        {
            var economy = new EconomyManager(50f);
            bool result = economy.Spend(100f);
            Assert.That(result, Is.False);
            Assert.That(economy.Money, Is.EqualTo(50f));
        }

        [Test]
        public void AddIncome_IncreasesMoney()
        {
            var economy = new EconomyManager();
            economy.AddIncome(500f);
            Assert.That(economy.Money, Is.EqualTo(50500f));
        }
    }
}
