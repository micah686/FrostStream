using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(53, "Create per-user media likes")]
public sealed class M053_CreateUserMediaLikes : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE IF NOT EXISTS media.user_media_likes
            (
                owner_subject text NOT NULL,
                media_guid uuid NOT NULL,
                liked_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT pk_user_media_likes PRIMARY KEY (owner_subject, media_guid),
                CONSTRAINT fk_user_media_likes_media_guid
                    FOREIGN KEY (media_guid)
                    REFERENCES media.media (media_guid)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_user_media_likes_owner_liked_at
                ON media.user_media_likes (owner_subject, liked_at DESC);
            """);
    }

    public override void Down()
    {
        Execute.Sql("DROP TABLE IF EXISTS media.user_media_likes;");
    }
}
