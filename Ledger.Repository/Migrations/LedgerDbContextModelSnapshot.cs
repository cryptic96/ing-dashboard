using System;
using Ledger.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ledger.Repository.Migrations
{
    [DbContext(typeof(LedgerDbContext))]
    partial class LedgerDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.12")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Ledger.Repository.Entities.DataProtectionCanaryEntity", b =>
                {
                    b.Property<short>("Id")
                        .HasColumnType("smallint")
                        .HasColumnName("id");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<byte[]>("PlaintextSha256")
                        .IsRequired()
                        .HasColumnType("bytea")
                        .HasColumnName("plaintext_sha256");

                    b.Property<string>("ProtectedPayload")
                        .IsRequired()
                        .HasColumnType("text")
                        .HasColumnName("protected_payload");

                    b.HasKey("Id")
                        .HasName("pk_data_protection_canary");

                    b.ToTable("data_protection_canary", t =>
                        {
                            t.HasCheckConstraint("ck_data_protection_canary_id", "id = 1");
                        });
                });

            modelBuilder.Entity("Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasColumnName("id");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("FriendlyName")
                        .HasColumnType("text")
                        .HasColumnName("friendly_name");

                    b.Property<string>("Xml")
                        .HasColumnType("text")
                        .HasColumnName("xml");

                    b.HasKey("Id")
                        .HasName("pk_data_protection_keys");

                    b.ToTable("data_protection_keys");
                });
#pragma warning restore 612, 618
        }
    }
}
