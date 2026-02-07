using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(1)]
public class InitialSchema : Migration
{
    public override void Up()
    {
        // Create movies table
        Create.Table("movies")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("title").AsString(500).NotNullable().Indexed()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("release_year").AsInt32().NotNullable().Indexed()
            .WithColumn("duration_minutes").AsInt32().NotNullable()
            .WithColumn("file_path").AsString(1000).NotNullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        // Create users table
        Create.Table("users")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("username").AsString(100).NotNullable().Unique()
            .WithColumn("email").AsString(255).NotNullable().Unique()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("last_login_at").AsDateTime().Nullable();

        // Create subtitles table
        Create.Table("subtitles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("movie_id").AsGuid().NotNullable()
                .ForeignKey("FK_subtitles_movies_movie_id", "movies", "id")
                .OnDelete(System.Data.Rule.Cascade)
            .WithColumn("language").AsString(50).NotNullable()
            .WithColumn("file_path").AsString(1000).NotNullable()
            .WithColumn("created_at").AsDateTime().NotNullable();

        // Create unique index on subtitles (movie_id, language)
        Create.Index("IX_subtitles_movie_id_language")
            .OnTable("subtitles")
            .OnColumn("movie_id").Ascending()
            .OnColumn("language").Ascending()
            .WithOptions().Unique();

        // Create user_favorite_movies join table (many-to-many)
        Create.Table("user_favorite_movies")
            .WithColumn("FavoriteMoviesId").AsGuid().NotNullable()
                .ForeignKey("FK_user_favorite_movies_movies_FavoriteMoviesId", "movies", "id")
                .OnDelete(System.Data.Rule.Cascade)
            .WithColumn("FavoritedByUsersId").AsGuid().NotNullable()
                .ForeignKey("FK_user_favorite_movies_users_FavoritedByUsersId", "users", "id")
                .OnDelete(System.Data.Rule.Cascade);

        // Create composite primary key
        Create.PrimaryKey("PK_user_favorite_movies")
            .OnTable("user_favorite_movies")
            .Columns("FavoriteMoviesId", "FavoritedByUsersId");

        // Create index on FavoritedByUsersId for reverse lookups
        Create.Index("IX_user_favorite_movies_FavoritedByUsersId")
            .OnTable("user_favorite_movies")
            .OnColumn("FavoritedByUsersId");
    }

    public override void Down()
    {
        Delete.Table("subtitles");
        Delete.Table("user_favorite_movies");
        Delete.Table("movies");
        Delete.Table("users");
    }
}
