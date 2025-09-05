using System;
using System.Net;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class InviteCodeGeneratorTests
    {
        private readonly byte[] _testGroupKey = new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 };
        private readonly byte[] _testPrivateKey = new byte[32];

        [Fact]
        public void GenerateCode_ProducesValidBase36String()
        {
            var code = InviteCodeGenerator.GenerateCode(IPAddress.Parse("192.168.1.100"), 7777, _testGroupKey, 12345, _testPrivateKey);

            Assert.NotEmpty(code);
            Assert.Matches("^[0-9A-Z]+$", code);
        }

        [Fact]
        public void DecodeCode_ReturnsOriginalValues()
        {
            var originalIP = IPAddress.Parse("192.168.1.100");
            var originalPort = 7777;
            var originalCounter = 12345L;

            var code = InviteCodeGenerator.GenerateCode(originalIP, originalPort, _testGroupKey, originalCounter, _testPrivateKey);
            var (decodedIP, decodedPort, decodedCounter) = InviteCodeGenerator.DecodeCode(code, _testGroupKey);

            Assert.Equal(originalIP, decodedIP);
            Assert.Equal(originalPort, decodedPort);
            Assert.Equal(originalCounter, decodedCounter);
        }

        [Fact]
        public void DecodeCode_WithWrongGroupKey_ThrowsException()
        {
            var wrongKey = new byte[32];
            var code = InviteCodeGenerator.GenerateCode(IPAddress.Parse("192.168.1.100"), 7777, _testGroupKey, 12345, _testPrivateKey);

            Assert.Throws<InvalidOperationException>(() => InviteCodeGenerator.DecodeCode(code, wrongKey));
        }

        [Fact]
        public void GenerateCode_SameInputsProduceSameCode()
        {
            var ip = IPAddress.Parse("10.0.0.1");
            var port = 8080;
            var counter = 999L;

            var code1 = InviteCodeGenerator.GenerateCode(ip, port, _testGroupKey, counter, _testPrivateKey);
            var code2 = InviteCodeGenerator.GenerateCode(ip, port, _testGroupKey, counter, _testPrivateKey);

            Assert.Equal(code1, code2);
        }

        [Fact]
        public void GenerateCode_DifferentCountersProduceDifferentCodes()
        {
            var ip = IPAddress.Parse("10.0.0.1");
            var port = 8080;

            var code1 = InviteCodeGenerator.GenerateCode(ip, port, _testGroupKey, 100, _testPrivateKey);
            var code2 = InviteCodeGenerator.GenerateCode(ip, port, _testGroupKey, 101, _testPrivateKey);

            Assert.NotEqual(code1, code2);
        }

        [Theory]
        [InlineData("127.0.0.1", 80)]
        [InlineData("192.168.1.1", 443)]
        [InlineData("10.0.0.1", 65535)]
        public void RoundTrip_WorksForVariousIPsAndPorts(string ipStr, int port)
        {
            var ip = IPAddress.Parse(ipStr);
            var counter = 42L;

            var code = InviteCodeGenerator.GenerateCode(ip, port, _testGroupKey, counter, _testPrivateKey);
            var (decodedIP, decodedPort, decodedCounter) = InviteCodeGenerator.DecodeCode(code, _testGroupKey);

            Assert.Equal(ip, decodedIP);
            Assert.Equal(port, decodedPort);
            Assert.Equal(counter, decodedCounter);
        }
    }
}