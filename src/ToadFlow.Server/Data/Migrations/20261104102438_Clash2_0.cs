using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Clash2_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "disciplines",
                table: "users",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_pending",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "position",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<string[]>(
                name: "modules",
                table: "agents",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clash_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    model_path = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_models", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clash_parts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    @object = table.Column<string>(name: "object", type: "text", nullable: true),
                    part = table.Column<string>(type: "text", nullable: true),
                    draft_json = table.Column<string>(type: "jsonb", nullable: false),
                    draft_version = table.Column<int>(type: "integer", nullable: false),
                    result_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_version = table.Column<int>(type: "integer", nullable: true),
                    approved_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    next_collision_number = table.Column<int>(type: "integer", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_parts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clash_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clash_uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    geometry_bytes = table.Column<long>(type: "bigint", nullable: false),
                    geometry_sha256 = table.Column<string>(type: "text", nullable: false),
                    parameters_bytes = table.Column<long>(type: "bigint", nullable: false),
                    parameters_sha256 = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_uploads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clash_user_states",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    previous_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_user_states", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_clash_user_states_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extracted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revit_year = table.Column<int>(type: "integer", nullable: false),
                    view_name = table.Column<string>(type: "text", nullable: false),
                    elements = table.Column<int>(type: "integer", nullable: false),
                    triangles = table.Column<long>(type: "bigint", nullable: false),
                    inventory = table.Column<int>(type: "integer", nullable: false),
                    geometry_bytes = table.Column<long>(type: "bigint", nullable: false),
                    parameters_bytes = table.Column<long>(type: "bigint", nullable: false),
                    catalog = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_snapshots_clash_models_model_id",
                        column: x => x.model_id,
                        principalTable: "clash_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_collision_stats",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline = table.Column<string>(type: "text", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<int>(type: "integer", nullable: false),
                    fake = table.Column<int>(type: "integer", nullable: false),
                    @new = table.Column<int>(name: "new", type: "integer", nullable: false),
                    @fixed = table.Column<int>(name: "fixed", type: "integer", nullable: false),
                    returned = table.Column<int>(type: "integer", nullable: false),
                    gone_by_rules = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_stats", x => new { x.run_id, x.discipline });
                    table.ForeignKey(
                        name: "fk_clash_collision_stats_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_collisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assignee = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    found_now = table.Column<bool>(type: "boolean", nullable: false),
                    model_a = table.Column<string>(type: "text", nullable: false),
                    unique_id_a = table.Column<string>(type: "text", nullable: false),
                    element_id_a = table.Column<long>(type: "bigint", nullable: false),
                    discipline_a = table.Column<string>(type: "text", nullable: false),
                    sets_a = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_a = table.Column<string>(type: "text", nullable: false),
                    family_a = table.Column<string>(type: "text", nullable: true),
                    type_a = table.Column<string>(type: "text", nullable: true),
                    level_a = table.Column<string>(type: "text", nullable: true),
                    model_b = table.Column<string>(type: "text", nullable: false),
                    unique_id_b = table.Column<string>(type: "text", nullable: false),
                    element_id_b = table.Column<long>(type: "bigint", nullable: false),
                    discipline_b = table.Column<string>(type: "text", nullable: false),
                    sets_b = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_b = table.Column<string>(type: "text", nullable: false),
                    family_b = table.Column<string>(type: "text", nullable: true),
                    type_b = table.Column<string>(type: "text", nullable: true),
                    level_b = table.Column<string>(type: "text", nullable: true),
                    discipline_pair = table.Column<string>(type: "text", nullable: false),
                    checks = table.Column<string[]>(type: "text[]", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    z = table.Column<double>(type: "double precision", nullable: false),
                    depth_mm = table.Column<float>(type: "real", nullable: false),
                    fingerprint_a = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint_b = table.Column<long>(type: "bigint", nullable: false),
                    first_version = table.Column<int>(type: "integer", nullable: false),
                    last_found_version = table.Column<int>(type: "integer", nullable: false),
                    last_found_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    suggested_predecessor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_collisions_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_draft_hits",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_pair = table.Column<string>(type: "text", nullable: false),
                    checks = table.Column<string[]>(type: "text[]", nullable: false),
                    model_a = table.Column<string>(type: "text", nullable: false),
                    unique_id_a = table.Column<string>(type: "text", nullable: false),
                    element_id_a = table.Column<long>(type: "bigint", nullable: false),
                    discipline_a = table.Column<string>(type: "text", nullable: false),
                    sets_a = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_a = table.Column<string>(type: "text", nullable: false),
                    family_a = table.Column<string>(type: "text", nullable: true),
                    type_a = table.Column<string>(type: "text", nullable: true),
                    level_a = table.Column<string>(type: "text", nullable: true),
                    model_b = table.Column<string>(type: "text", nullable: false),
                    unique_id_b = table.Column<string>(type: "text", nullable: false),
                    element_id_b = table.Column<long>(type: "bigint", nullable: false),
                    discipline_b = table.Column<string>(type: "text", nullable: false),
                    sets_b = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_b = table.Column<string>(type: "text", nullable: false),
                    family_b = table.Column<string>(type: "text", nullable: true),
                    type_b = table.Column<string>(type: "text", nullable: true),
                    level_b = table.Column<string>(type: "text", nullable: true),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    z = table.Column<double>(type: "double precision", nullable: false),
                    depth_mm = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_draft_hits", x => new { x.run_id, x.number });
                    table.ForeignKey(
                        name: "fk_clash_draft_hits_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_draft_versions",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_draft_versions", x => new { x.part_id, x.version });
                    table.ForeignKey(
                        name: "fk_clash_draft_versions_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_engine_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: true),
                    trigger_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unchecked_models = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    previous_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    models = table.Column<string>(type: "jsonb", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stage = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    stats = table.Column<string>(type: "jsonb", nullable: true),
                    hit_count = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_engine_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_engine_runs_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_part_models",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_override = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_part_models", x => new { x.part_id, x.model_id });
                    table.ForeignKey(
                        name: "fk_clash_part_models_clash_models_model_id",
                        column: x => x.model_id,
                        principalTable: "clash_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_clash_part_models_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_part_versions",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    models_json = table.Column<string>(type: "jsonb", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_part_versions", x => new { x.part_id, x.version });
                    table.ForeignKey(
                        name: "fk_clash_part_versions_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_run_pair_stats",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_pair = table.Column<string>(type: "text", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    found = table.Column<int>(type: "integer", nullable: false),
                    @new = table.Column<int>(name: "new", type: "integer", nullable: false),
                    returned = table.Column<int>(type: "integer", nullable: false),
                    @fixed = table.Column<int>(name: "fixed", type: "integer", nullable: false),
                    gone_by_rules = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_run_pair_stats", x => new { x.run_id, x.discipline_pair });
                    table.ForeignKey(
                        name: "fk_clash_run_pair_stats_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_collision_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bulk_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_collision_events_clash_collisions_collision_id",
                        column: x => x.collision_id,
                        principalTable: "clash_collisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_collision_samples",
                columns: table => new
                {
                    collision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    depth_mm = table.Column<float>(type: "real", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    z = table.Column<double>(type: "double precision", nullable: false),
                    fingerprint_a = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint_b = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_samples", x => new { x.collision_id, x.run_id });
                    table.ForeignKey(
                        name: "fk_clash_collision_samples_clash_collisions_collision_id",
                        column: x => x.collision_id,
                        principalTable: "clash_collisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_collision_seen",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_seen", x => new { x.user_id, x.collision_id });
                    table.ForeignKey(
                        name: "fk_clash_collision_seen_clash_collisions_collision_id",
                        column: x => x.collision_id,
                        principalTable: "clash_collisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_clash_collision_seen_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_approved_hits",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_pair = table.Column<string>(type: "text", nullable: false),
                    checks = table.Column<string[]>(type: "text[]", nullable: false),
                    model_a = table.Column<string>(type: "text", nullable: false),
                    unique_id_a = table.Column<string>(type: "text", nullable: false),
                    element_id_a = table.Column<long>(type: "bigint", nullable: false),
                    discipline_a = table.Column<string>(type: "text", nullable: false),
                    sets_a = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_a = table.Column<string>(type: "text", nullable: false),
                    family_a = table.Column<string>(type: "text", nullable: true),
                    type_a = table.Column<string>(type: "text", nullable: true),
                    level_a = table.Column<string>(type: "text", nullable: true),
                    model_b = table.Column<string>(type: "text", nullable: false),
                    unique_id_b = table.Column<string>(type: "text", nullable: false),
                    element_id_b = table.Column<long>(type: "bigint", nullable: false),
                    discipline_b = table.Column<string>(type: "text", nullable: false),
                    sets_b = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_b = table.Column<string>(type: "text", nullable: false),
                    family_b = table.Column<string>(type: "text", nullable: true),
                    type_b = table.Column<string>(type: "text", nullable: true),
                    level_b = table.Column<string>(type: "text", nullable: true),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    z = table.Column<double>(type: "double precision", nullable: false),
                    depth_mm = table.Column<float>(type: "real", nullable: false),
                    fingerprint_a = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint_b = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_approved_hits", x => new { x.run_id, x.number });
                    table.ForeignKey(
                        name: "fk_clash_approved_hits_clash_engine_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "clash_engine_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_events_collision_id_at",
                table: "clash_collision_events",
                columns: new[] { "collision_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_events_run_id",
                table: "clash_collision_events",
                column: "run_id",
                filter: "run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_seen_collision_id",
                table: "clash_collision_seen",
                column: "collision_id");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_stats_part_id_discipline_at",
                table: "clash_collision_stats",
                columns: new[] { "part_id", "discipline", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_checks",
                table: "clash_collisions",
                column: "checks")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_model_a",
                table: "clash_collisions",
                column: "model_a");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_model_b",
                table: "clash_collisions",
                column: "model_b");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_last_found_run_id",
                table: "clash_collisions",
                columns: new[] { "part_id", "last_found_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_model_a_unique_id_a_model_b_unique",
                table: "clash_collisions",
                columns: new[] { "part_id", "model_a", "unique_id_a", "model_b", "unique_id_b" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_number",
                table: "clash_collisions",
                columns: new[] { "part_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_status",
                table: "clash_collisions",
                columns: new[] { "part_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_draft_hits_checks",
                table: "clash_draft_hits",
                column: "checks")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_clash_draft_hits_part_id",
                table: "clash_draft_hits",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_clash_draft_hits_part_id_discipline_pair",
                table: "clash_draft_hits",
                columns: new[] { "part_id", "discipline_pair" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_one_queued_approved",
                table: "clash_engine_runs",
                column: "part_id",
                unique: true,
                filter: "kind = 'Approved' AND state = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_part_id_created_at",
                table: "clash_engine_runs",
                columns: new[] { "part_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_part_id_trigger_run_id",
                table: "clash_engine_runs",
                columns: new[] { "part_id", "trigger_run_id" },
                unique: true,
                filter: "trigger_run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_state_created_at",
                table: "clash_engine_runs",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_models_key",
                table: "clash_models",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clash_part_models_model_id",
                table: "clash_part_models",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "ix_clash_run_pair_stats_part_id",
                table: "clash_run_pair_stats",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_clash_snapshots_model_id_extracted_at",
                table: "clash_snapshots",
                columns: new[] { "model_id", "extracted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_templates_name",
                table: "clash_templates",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clash_uploads_created_at",
                table: "clash_uploads",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_clash_uploads_job_id",
                table: "clash_uploads",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_approved_hits");

            migrationBuilder.DropTable(
                name: "clash_collision_events");

            migrationBuilder.DropTable(
                name: "clash_collision_samples");

            migrationBuilder.DropTable(
                name: "clash_collision_seen");

            migrationBuilder.DropTable(
                name: "clash_collision_stats");

            migrationBuilder.DropTable(
                name: "clash_draft_hits");

            migrationBuilder.DropTable(
                name: "clash_draft_versions");

            migrationBuilder.DropTable(
                name: "clash_part_models");

            migrationBuilder.DropTable(
                name: "clash_part_versions");

            migrationBuilder.DropTable(
                name: "clash_run_pair_stats");

            migrationBuilder.DropTable(
                name: "clash_snapshots");

            migrationBuilder.DropTable(
                name: "clash_templates");

            migrationBuilder.DropTable(
                name: "clash_uploads");

            migrationBuilder.DropTable(
                name: "clash_user_states");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "clash_engine_runs");

            migrationBuilder.DropTable(
                name: "clash_collisions");

            migrationBuilder.DropTable(
                name: "clash_models");

            migrationBuilder.DropTable(
                name: "clash_parts");

            migrationBuilder.DropColumn(
                name: "disciplines",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "is_pending",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "users");

            migrationBuilder.DropColumn(
                name: "position",
                table: "users");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                table: "users");

            migrationBuilder.DropColumn(
                name: "modules",
                table: "agents");
        }
    }
}
