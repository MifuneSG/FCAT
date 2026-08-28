from django.contrib import admin

from .models import ApiKey


@admin.register(ApiKey)
class ApiKeyAdmin(admin.ModelAdmin):
    """Oversight only. Keys are made by FCs on their own page, not handed out from here,
    so this exists so an admin can see who has connected and cut one off."""

    list_display = ("user", "name", "prefix", "created_at", "last_used_at", "is_active")
    list_filter = ("is_active",)
    search_fields = ("user__username", "name")
    readonly_fields = ("user", "name", "prefix", "created_at", "last_used_at")
    actions = ("revoke",)

    def has_add_permission(self, request):
        return False

    @admin.action(description="Revoke selected keys")
    def revoke(self, request, queryset):
        count = queryset.update(is_active=False)
        self.message_user(request, f"Revoked {count} key(s).")
