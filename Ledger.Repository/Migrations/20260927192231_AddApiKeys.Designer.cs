using System;
using Ledger.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ledger.Repository.Migrations
{
    [DbContext(typeof(LedgerDbContext))]
    [Migration("20260927192231_AddApiKeys")]
    partial class AddApiKeys
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.12")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Ledger.Repository.Entities.ApiKeyEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<string>("KeyId")
                        .IsRequired()
                        .HasMaxLength(16)
                        .HasColumnType("character varying(16)")
                        .HasColumnName("key_id");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)")
                        .HasColumnName("name");

                    b.Property<DateTimeOffset?>("RevokedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("revoked_at");

                    b.Property<byte[]>("SecretSha256")
                        .IsRequired()
                        .HasColumnType("bytea")
                        .HasColumnName("secret_sha256");

                    b.HasKey("Id")
                        .HasName("pk_api_keys");

                    b.HasIndex("KeyId")
                        .IsUnique()
                        .HasDatabaseName("ix_api_keys_key_id");

                    b.HasIndex("Name")
                        .IsUnique()
                        .HasDatabaseName("ix_api_keys_name")
                        .HasFilter("revoked_at IS NULL");

                    b.ToTable("api_keys");
                });

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
