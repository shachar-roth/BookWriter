# macOS validation checklist

Run this checklist on a current macOS machine after copying or cloning the application source.

1. Install the .NET 8 SDK and Git, then run `dotnet restore`, `dotnet build --no-restore`, and `dotnet test --no-build --no-restore`.
2. Start the app with `dotnet run` and open the printed local URL in Safari or Chrome.
3. Create a project and confirm the native folder chooser opens, the selected folder receives `Scenes`, `Indexes`, `.studio`, and `.git`, and two initial commits exist after metadata migration.
4. Open an existing project and verify recent-project selection, continuous scrolling, chapter navigation, metadata drawers, and local draft recovery after closing a tab while typing.
5. Open assistant settings, save an API key, and confirm macOS Keychain contains a generic password named `israeli-author-studio:llm`. Confirm the key does not appear in project files or `git status`.
6. Configure an inexpensive metadata model and a chat model, verify streamed chat and cancellation, and approve then undo a harmless proposal.
7. Attach an SSH or HTTPS test remote, wait for an idle snapshot, and verify commit, fetch, rebase, and push. Disconnect the network and confirm local commits continue and retry status appears.
8. Clone the remote through the project screen into an empty folder and verify the cloned story opens.
9. Create conflicting edits on two machines, verify automatic rebase aborts on conflict, local scene text is unchanged, and sync pauses without force-pushing.
10. Close the final editor tab and then stop the app normally. Confirm each event schedules a final local snapshot without displaying an unhandled error.
11. Install an older tagged release, publish a newer patch release, and relaunch the app. Confirm the update notification appears after the package downloads.
12. Choose the restart-and-update action. Confirm the browser opens again, the version under `Israeli Author Studio.app/Contents/Info.plist` changed, and existing projects and assistant settings remain available.
13. Review `~/Library/Application Support/IsraeliAuthorStudio/Logs/updater.log` and confirm the update completed successfully.
