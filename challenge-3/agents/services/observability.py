"""Minimal observability helpers shared by Challenge 3 agents."""

from __future__ import annotations

from typing import Optional


def enable_tracing(app_insights_connection: Optional[str]) -> None:
    """Enable Agent Framework tracing (Azure Monitor exporter) if available."""

    try:
        from agent_framework.observability import configure_otel_providers
        from azure.monitor.opentelemetry.exporter import (
            AzureMonitorLogExporter,
            AzureMonitorMetricExporter,
            AzureMonitorTraceExporter,
        )
    except ImportError:
        print("⚠️  Agent Framework observability not available.")
        return

    if not app_insights_connection:
        print("⚠️  Tracing available but APPLICATIONINSIGHTS_CONNECTION_STRING not set\n")
        return

    try:
        trace_exporter = AzureMonitorTraceExporter.from_connection_string(
            app_insights_connection)
        metric_exporter = AzureMonitorMetricExporter.from_connection_string(
            app_insights_connection)
        log_exporter = AzureMonitorLogExporter.from_connection_string(
            app_insights_connection)

        configure_otel_providers(
            enable_sensitive_data=True,  # Capture prompts and completions
            exporters=[trace_exporter, metric_exporter, log_exporter],
        )
        print("📊 Agent Framework tracing enabled (Azure Monitor)")
        print(f"   Traces sent to: {app_insights_connection.split(';')[0]}")
        print("   View in Azure AI Foundry portal: https://ai.azure.com -> Your Project -> Tracing\n")
    except Exception as e:
        print(f"⚠️  Tracing setup failed: {e}\n")
