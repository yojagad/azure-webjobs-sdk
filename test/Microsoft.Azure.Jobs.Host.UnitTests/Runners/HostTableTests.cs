﻿using System;
using System.Collections.Generic;
using Microsoft.Azure.Jobs.Host.Runners;
using Microsoft.Azure.Jobs.Host.Storage.Table;
using Microsoft.Azure.Jobs.Host.TestCommon;
using Microsoft.WindowsAzure.Storage;
using Moq;
using Xunit;

namespace Microsoft.Azure.Jobs.Host.UnitTests.Runners
{
    public class HostTableTests
    {
        [Fact]
        public void ConstructorWithICloudTable_IfTableIsNull_Throws()
        {
            // Arrange
            ICloudTable table = null;

            // Act & Assert
            ExceptionAssert.ThrowsArgumentNull(() => CreateProductUnderTest(table), "table");
        }

        [Fact]
        public void ConstructorWithICloudTableClient_IfClientIsNull_Throws()
        {
            // Arrange
            ICloudTableClient client = null;

            // Act & Assert
            ExceptionAssert.ThrowsArgumentNull(() => CreateProductUnderTest(client), "client");
        }

        [Fact]
        public void Table_IfUsingICloudTableConstructor_ReturnsSpecifiedInstance()
        {
            // Arrange
            ICloudTable expectedTable = CreateDummyTable();
            HostTable product = CreateProductUnderTest(expectedTable);

            // Act
            ICloudTable table = product.Table;

            // Assert
            Assert.Same(expectedTable, table);
        }

        [Fact]
        public void Table_IfUsingICloudTableClientConstructor_ReturnsHostsTable()
        {
            // Arrange
            ICloudTable expectedTable = CreateDummyTable();

            Mock<ICloudTableClient> clientMock = new Mock<ICloudTableClient>();
            clientMock
                .Setup(c => c.GetTableReference(HostTableNames.HostsTableName))
                .Returns(expectedTable);
            ICloudTableClient client = clientMock.Object;
            HostTable product = CreateProductUnderTest(client);

            // Act
            ICloudTable table = product.Table;

            // Assert
            Assert.Same(expectedTable, table);
        }

        [Fact]
        public void GetOrCreateHostId_UsesTableGetOrInsert()
        {
            // Arrange
            string hostName = "IgnoreHostName";
            Guid expectedHostId = Guid.NewGuid();
            List<HostEntity> entitiesAdded = new List<HostEntity>();

            Mock<ICloudTable> tableMock = new Mock<ICloudTable>();
            tableMock
                .Setup(t => t.GetOrInsert(It.Is<HostEntity>(e => e.PartitionKey == hostName
                    && e.RowKey == String.Empty
                    && e.Id != Guid.Empty)))
                .Returns<HostEntity>((e) => new HostEntity
                {
                    PartitionKey = e.PartitionKey,
                    RowKey = e.RowKey,
                    Id = expectedHostId
                });
            ICloudTable table = tableMock.Object;

            IHostTable product = CreateProductUnderTest(table);

            // Act
            Guid hostId = product.GetOrCreateHostId(hostName);

            // Assert
            Assert.Equal(expectedHostId, hostId);
        }

        private static ICloudTable CreateDummyTable()
        {
            return new Mock<ICloudTable>(MockBehavior.Strict).Object;
        }

        private static HostTable CreateProductUnderTest(ICloudTable table)
        {
            return new HostTable(table);
        }

        private static HostTable CreateProductUnderTest(ICloudTableClient client)
        {
            return new HostTable(client);
        }

        private static StorageException CreateStorageException(int httpStatusCode)
        {
            return CreateStorageException(new RequestResult { HttpStatusCode = httpStatusCode });
        }

        private static StorageException CreateStorageException(RequestResult requestInformation)
        {
            return new StorageException(requestInformation, null, null);
        }
    }
}
