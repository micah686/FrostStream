using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

/// <summary>
/// The live-chat replay feature added <c>DownloadStage.LiveChatUpload</c>, but the
/// <c>jobs.download_stage</c> enum was never extended, so persisting a live-chat artifact
/// failed with 22P02.
/// </summary>
[Migration(97, TransactionBehavior.None, "Add 'live_chat_upload' download stage for live chat replay artifacts")]
public sealed class M097_AddLiveChatUploadDownloadStage : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TYPE jobs.download_stage ADD VALUE IF NOT EXISTS 'live_chat_upload';");
    }

    public override void Down()
    {
        // PostgreSQL enum labels cannot be dropped safely.
    }
}
