## Step-by-Step Database and Login Diagnostics

This file provides detailed instructions for troubleshooting your login issue.

### The Problem
When you try to log in with credentials:
- WorkerId: ADMIN-0001
- Password: MarkMccain2323!

The text boxes turn red (validation error) but no error message is displayed.

### STEP 1: Run Diagnostics

To enable detailed diagnostics, add the following code to your `TakOne.WebUI\Program.cs` file AFTER the `await DefaultAdminSeeder.EnsureDefaultAdminAsync(app.Services);` line:

```csharp
// Run login diagnostics
if (app.Environment.IsDevelopment())
{
	using (var scope = app.Services.CreateScope())
	{
		var logger = scope.ServiceProvider
			.GetRequiredService<ILoggerFactory>()
			.CreateLogger("Program");

		logger.LogInformation("Running login diagnostics...");

		await TakOne.WebUI.Diagnostics.LoginDiagnostics.RunDiagnosticsAsync(
			app.Services,
			"ADMIN-0001",
			"MarkMccain2323!");
	}
}
```

Then:
1. Clean the database: Delete the `.mdf` and `.ldf` files for your LocalDB (usually in `C:\Users\YourUserName\AppData\Local\Microsoft\Microsoft SQL Server Local DB\Instances\MSSQLLocalDB\`)
   OR run: `dotnet ef database drop --force` 
2. Run `dotnet ef database update` to recreate the database
3. Run the application with `dotnet run`
4. Check the **Output window** in Visual Studio (Build pane or Debug Output) for the diagnostics results

### STEP 2: Understand the Diagnostics Output

The diagnostics will check:

1. **ApplicationUser Lookup** - Does the user exist in AspNetUsers table?
2. **Domain User Lookup** - Does the user exist in DomainUsers table?
3. **Password Validation** - Is the password hash correct? Does the provided password match?
   - ✅ Success = Password is correct
   - ⚠️  SuccessRehashNeeded = Password is correct but needs re-hashing
   - ❌ Failed = Password is WRONG
4. **User Roles** - Does the user have the Admin role assigned?
5. **Lockout Status** - Is the user locked out due to failed attempts?

### STEP 3: Locate Your Output Logs

In Visual Studio:
- **Debug Output Window**: View > Output > "Debug" dropdown
- Look for lines starting with "LoginDiagnostics" or "========== LOGIN DIAGNOSTICS"

### STEP 4: Common Issues Found

#### Issue 1: ApplicationUser not found
**Problem**: User doesn't exist in database
**Solution**: 
- Verify migration ran successfully
- Check that DefaultAdminSeeder.EnsureDefaultAdminAsync executed without errors
- The seeder creates user only if NO admin users exist

#### Issue 2: Password verification Failed
**Problem**: Password hash doesn't match the provided password
**Solution**:
- The DefaultAdminSeeder hardcodes: `public const string DefaultPassword = "MarkMccain2323!";`
- If you see "Password is INCORRECT", verify you're typing: **MarkMccain2323!** exactly
- Check for CAPS LOCK or extra spaces
- The seeder MUST have run successfully to set the password

#### Issue 3: User has no roles
**Problem**: User exists but has no Admin role assigned
**Solution**:
- Run RoleSeeder first (Program.cs does this before DefaultAdminSeeder)
- The DefaultAdminSeeder assigns the Admin role during user creation
- If the role assignment failed, you'll see the error in logs

#### Issue 4: User is locked out
**Problem**: AccessFailedCount >= MaxFailedAccessAttempts (5 by default)
**Solution**:
- Stop the app
- Open SQL Server Management Studio or Azure Data Studio
- Run: `UPDATE AspNetUsers SET AccessFailedCount = 0, LockoutEnd = NULL WHERE UserName = 'ADMIN-0001'`
- Restart the app

### STEP 5: Check Login Logs During Attempt

After diagnostics pass, try logging in again:

1. Open **Output window** (View > Output)
2. Change dropdown to **Debug** if not already selected
3. Look for log entries starting with "Login attempt started for WorkerId: ADMIN-0001"
4. Follow the logs through each step:
   - "HttpContext acquired"
   - "ApplicationUser found"
   - "Attempting PasswordSignInAsync"
   - "PasswordSignInAsync result"
   - Look for any errors with red text

### STEP 6: If Diagnostics Show Everything is OK

If diagnostics show:
- ✅ ApplicationUser found
- ✅ Domain User found  
- ✅ Password is CORRECT
- ✅ User has Admin role
- ✅ User is not locked out

But login still fails, the issue is likely:

1. **Exception in SignInWithClaimsAsync** - An error when re-signing with claims
   - Check logs for "Unexpected exception during login"
   - This might be a claim formatting issue or missing dependency

2. **Database state issue** - Try a clean reset:
   - Delete all `.mdf` and `.ldf` files
   - Run `dotnet ef database drop --force` && `dotnet ef database update`
   - This ensures a completely fresh database

3. **SignalR/Circuit issue** - The form might be in a Blazor circuit instead of static SSR
   - Verify Login.razor page directive: `@page "/Account/Login"`
   - Check that it uses `@layout LoginLayout` (static, not interactive)

### STEP 7: Extract the Real Error

Once you have diagnostics output, look for these patterns in logs:

```
❌ ERROR: [description]
⚠️  [warning description]
Unexpected exception during login for WorkerId: ADMIN-0001
```

**Copy the error message and let me know:**
- The exact error text
- Whether it's from diagnostics or from a login attempt
- The full stack trace if available (shown in logs with "Exception:" at the beginning)

---

**Example Diagnostic Output (Healthy State):**

```
========== LOGIN DIAGNOSTICS START ==========
WorkerId: ADMIN-0001, Password Length: 17
1. Looking up ApplicationUser by WorkerId: ADMIN-0001
✅ ApplicationUser found:
   - Id: [guid]
   - UserName: ADMIN-0001
   - Email: admin@takone.local
   - EmailConfirmed: True
   - IsActive: True
   - PasswordHash exists: True
2. Looking up Domain User by Id: [guid]
✅ Domain User found:
   - WorkerId: ADMIN-0001
   - FullName: System Administrator
3. Testing password validation
   Password verification result: Success
✅ Password is CORRECT (Success)
4. Checking user roles
✅ User roles:
   - Admin
5. Checking lockout status
✅ User is not locked out
========== LOGIN DIAGNOSTICS END ==========
```

If your output matches this pattern, the database is healthy and we need to investigate the SignInWithClaimsAsync call or form handling.
