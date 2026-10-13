using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;

namespace DocsAssistant.Application.Tests;

public sealed class TenantContextTests
{
	[Fact]
	public void StartsUnbound()
	{
		Assert.Null(new TenantContext().CurrentTenantId);
	}

	[Fact]
	public void Set_BindsTheTenant()
	{
		var context = new TenantContext();
		var tenant = TenantId.New();

		context.Set(tenant);

		Assert.Equal(tenant, context.CurrentTenantId);
	}

	[Fact]
	public void Set_SameTenantAgain_IsAllowed()
	{
		var context = new TenantContext();
		var tenant = TenantId.New();
		context.Set(tenant);

		context.Set(tenant);

		Assert.Equal(tenant, context.CurrentTenantId);
	}

	[Fact]
	public void Set_DifferentTenant_Throws()
	{
		var context = new TenantContext();
		context.Set(TenantId.New());

		Assert.Throws<InvalidOperationException>(() => context.Set(TenantId.New()));
	}
}
