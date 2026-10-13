using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Tenants;

namespace DocsAssistant.Domain.Tests;

public sealed class TenantTests
{
	private static Tenant NewTenant(params string[] origins) =>
		Tenant.Create(
			"Demo",
			"pk_demo",
			"owner@example.com",
			ContentLanguage.Spanish,
			origins,
			DateTimeOffset.UnixEpoch);

	[Theory]
	[InlineData("https://juangomezb.com")]
	[InlineData("https://JuanGomezB.com")]
	[InlineData("https://juangomezb.com/")]
	[InlineData("https://juangomezb.com:443")]
	public void IsOriginAllowed_MatchesNormalizedOrigins(string origin) =>
		Assert.True(NewTenant("https://juangomezb.com").IsOriginAllowed(origin));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("https://evil.com")]
	[InlineData("http://juangomezb.com")]
	[InlineData("https://juangomezb.com.evil.com")]
	[InlineData("https://www.juangomezb.com")]
	public void IsOriginAllowed_RejectsEverythingElse(string? origin) =>
		Assert.False(NewTenant("https://juangomezb.com").IsOriginAllowed(origin));

	[Fact]
	public void IsOriginAllowed_WithNoOrigins_RejectsAll() =>
		Assert.False(NewTenant().IsOriginAllowed("https://juangomezb.com"));

	[Theory]
	[InlineData("juangomezb.com")]
	[InlineData("ftp://juangomezb.com")]
	[InlineData("https://juangomezb.com/path")]
	public void AllowOrigin_RejectsNonOrigins(string origin) =>
		Assert.Throws<DomainException>(() => NewTenant(origin));

	[Fact]
	public void AllowOrigin_IsIdempotent()
	{
		var tenant = NewTenant("https://a.com", "https://A.com/");

		Assert.Single(tenant.AllowedOrigins);
	}
}
