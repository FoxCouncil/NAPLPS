// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSTests.File;

[TestClass]
public class FileLoadingTests
{
    [TestMethod]
    public void LoadMaple()
    {
        var file = NaplpsFormat.FromFile("examples/maple.nap");

        Assert.IsNotNull(file);

        Assert.IsFalse(file.IsErrored);
        Assert.IsTrue(file.IsValid);

        Assert.IsTrue(file.Is7Bit);

        Assert.AreEqual(11, file.Commands.Count);
    }

    [TestMethod]
    public void LoadAutumn()
    {
        var file = NaplpsFormat.FromFile("examples/autumn.nap");

        Assert.IsNotNull(file);

        Assert.IsFalse(file.IsErrored);
        Assert.IsTrue(file.IsValid);

        Assert.IsTrue(file.Is7Bit);

        Assert.AreEqual(300, file.Commands.Count);
    }

    [TestMethod]
    public void LoadGirl()
    {
        var file = NaplpsFormat.FromFile("examples/girl.nap");

        Assert.IsNotNull(file);

        Assert.IsFalse(file.IsErrored);
        Assert.IsTrue(file.IsValid);

        Assert.IsTrue(file.Is7Bit);
        Assert.AreEqual(NaplpsSystemType.NAPLPS, file.SystemType);

        // 1662 -> 1665 when ESC-coded DEF TEXTURE started buffering its mask bodies: each
        // definition serializes as raw body bytes plus an explicit END sequence.
        Assert.AreEqual(1665, file.Commands.Count);
    }

    [TestMethod]
    public void LoadBuilding()
    {
        var file = NaplpsFormat.FromFile("examples/building.nap");

        Assert.IsNotNull(file);

        Assert.IsFalse(file.IsErrored);
        Assert.IsTrue(file.IsValid);

        Assert.IsTrue(file.Is7Bit);
        Assert.AreEqual(NaplpsSystemType.NAPLPS, file.SystemType);

        Assert.AreEqual(1633, file.Commands.Count);

        // building.nap contains blink commands
        Assert.IsTrue(file.State.BlinkProcesses.Count > 0);
    }

    [TestMethod]
    public void LoadCar()
    {
        var file = NaplpsFormat.FromFile("examples/car.nap");

        Assert.IsNotNull(file);

        Assert.IsFalse(file.IsErrored);
        Assert.IsTrue(file.IsValid);

        Assert.IsTrue(file.Is7Bit);
        Assert.AreEqual(NaplpsSystemType.NAPLPS, file.SystemType);

        // 1475 -> 1485 when ESC-coded DEF TEXTURE started buffering its mask bodies (see
        // LoadGirl).
        Assert.AreEqual(1485, file.Commands.Count);
    }

    [TestMethod]
    public void LoadAudi1()
    {
        var file = NaplpsFormat.FromFile("examples/Anthony Wetzel/AUDI1.nap");

        Assert.IsNotNull(file);

        Assert.IsFalse(file.IsErrored);
        Assert.IsTrue(file.IsValid);

        Assert.IsFalse(file.Is7Bit);
        Assert.AreEqual(NaplpsSystemType.Prodigy, file.SystemType);

        Assert.AreEqual(457, file.Commands.Count);
    }

    [TestMethod]
    public void LoadCoke1()
    {
        var file = NaplpsFormat.FromFile("examples/Anthony Wetzel/COKE1.nap");

        Assert.IsNotNull(file);

        Assert.IsFalse(file.IsErrored);
        Assert.IsTrue(file.IsValid);

        Assert.IsFalse(file.Is7Bit);
        Assert.AreEqual(NaplpsSystemType.Prodigy, file.SystemType);

        Assert.AreEqual(130, file.Commands.Count);
    }
}
