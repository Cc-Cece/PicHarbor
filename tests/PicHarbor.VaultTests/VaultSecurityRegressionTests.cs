using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

public sealed class VaultSecurityRegressionTests
{
    [Fact]
    public void Restart_relocks_root_and_explicit_child_permissions()
    {
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.SkipUnless(new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),"Requires an elevated isolated ACL test process.");
        using var scope=new VaultTestScope();
        string basePath=Path.Combine(Path.GetPathRoot(scope.Directory)!,"PicHarborTestArchives","Restart");
        string root=Path.Combine(basePath,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"photo.txt"),"isolated test data");
        try
        {
            using var first=new VaultState();Assert.True(first.TrySetPassword("test-password",out _));
            Assert.True(first.TryRegister(root,out string registerError),registerError);
            Assert.True(first.TryUnlock("test-password",identity.User!,Environment.ProcessId,out string unlockError),unlockError);
            using var restarted=new VaultState();restarted.RevokeAllOnStartup();
            Assert.Empty(restarted.ProtectionErrors);Assert.False(restarted.IsUnlocked);
            var acl=new DirectoryInfo(root).GetAccessControl();
            Assert.DoesNotContain(acl.GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>(),r=>r.IdentityReference.Equals(identity.User));
            Assert.True(restarted.TryUnregister(root,identity.User!,out string restoreError),restoreError);
        }
        finally
        {
            AclSnapshotStore.Restore(root,identity.User!);
            string resolved=Path.GetFullPath(root);
            if(resolved.StartsWith(Path.GetFullPath(basePath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved,true);
        }
    }
    [Fact]
    public void Startup_records_protection_failure_for_disconnected_root()
    {
        using var scope=new VaultTestScope();
        string absent=Path.Combine(Path.GetPathRoot(scope.Directory)!,"PicHarborTestArchives","Absent",Guid.NewGuid().ToString("N"));
        RootStore.Save([absent]);
        using var state=new VaultState();state.RevokeAllOnStartup();
        Assert.Contains(absent,state.RegisteredRoots);
        Assert.Contains(absent,state.ProtectionErrors.Keys);
        Assert.False(state.IsUnlocked);
    }

    [Fact]
    public void Failed_restore_keeps_registration_and_password()
    {
        using var scope=new VaultTestScope();
        string absent=Path.Combine(Path.GetPathRoot(scope.Directory)!,"PicHarborTestArchives","Absent",Guid.NewGuid().ToString("N"));
        RootStore.Save([absent]);
        using var state=new VaultState();Assert.True(state.TrySetPassword("test-password",out _));
        using var user=System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.False(state.TryUnregister(absent,user.User!,out _));
        Assert.Contains(absent,state.RegisteredRoots);Assert.Contains(absent,RootStore.Load());
        Assert.False(state.TryClearPassword(user.User!,out _));
        Assert.True(state.HasPassword);Assert.Contains(absent,RootStore.Load());
    }

    [Fact]
    public async Task Simultaneous_totp_password_resets_accept_the_code_only_once()
    {
        using var scope=new VaultTestScope();using var state=new VaultState();
        byte[] secret=TotpEngine.GenerateSecret();TotpStore.SetSecret(secret);
        string code=TotpEngine.ComputeCode(secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var gate=new ManualResetEventSlim(false);
        var tasks=Enumerable.Range(0,8).Select(i=>Task.Run(()=>
        { gate.Wait(TestContext.Current.CancellationToken);return state.TryResetPasswordWithTotp(code,"password-"+i,out _); },TestContext.Current.CancellationToken)).ToArray();
        gate.Set();var accepted=await Task.WhenAll(tasks);
        Assert.Equal(1,accepted.Count(a=>a));
    }

    [Fact]
    public void Verification_persistence_failure_cannot_report_password_reset_success()
    {
        using var scope=new VaultTestScope();using var state=new VaultState();
        byte[] secret=TotpEngine.GenerateSecret();TotpStore.SetSecret(secret);
        state.TrySetPassword("old-password",out _);
        string code=TotpEngine.ComputeCode(secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        var directory=new DirectoryInfo(scope.Directory);var original=directory.GetAccessControl();var blocked=directory.GetAccessControl();
        blocked.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User!,System.Security.AccessControl.FileSystemRights.CreateFiles,System.Security.AccessControl.AccessControlType.Deny));
        directory.SetAccessControl(blocked);
        try
        {
            Assert.ThrowsAny<IOException>(()=>state.TryResetPasswordWithTotp(code,"new-password",out _));
            Assert.True(PasswordStore.Verify("old-password"));
        }
        finally { directory.SetAccessControl(original); }
    }

    [Fact]
    public void User_owned_protection_data_is_never_trusted()
    {
        using var scope=new VaultTestScope();File.WriteAllText(Path.Combine(scope.Directory,"vault_roots.json"),"[]");
        Assert.Throws<InvalidDataException>(()=>VaultStore.ValidateProtectedFiles(scope.Directory));
    }

    [Fact]
    public void Service_image_rejects_precreated_untrusted_directory()
    {
        using var scope=new VaultTestScope();string source=Path.Combine(scope.Directory,"source.exe");File.WriteAllText(source,"isolated dummy image");
        string target=Path.Combine(scope.Directory,"untrusted");Directory.CreateDirectory(target);
        Assert.Throws<InvalidDataException>(()=>ServiceImageStore.Prepare(source,target));
    }

    [Fact]
    public void Elevated_service_image_is_copied_to_system_owned_private_storage()
    {
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.SkipUnless(new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),"Requires an elevated isolated staging test.");
        using var scope=new VaultTestScope();string sourceDir=Path.Combine(scope.Directory,"source");Directory.CreateDirectory(sourceDir);
        string executable=Path.Combine(sourceDir,"service.exe");File.WriteAllText(executable,"isolated dummy image");
        string destination=Path.Combine(scope.Directory,"protected-service");
        string staged=ServiceImageStore.Prepare(executable,destination);
        Assert.NotEqual(executable,staged);Assert.Equal(File.ReadAllBytes(executable),File.ReadAllBytes(staged));
        var acl=new FileInfo(staged).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid,null),acl.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)));
    }
}
