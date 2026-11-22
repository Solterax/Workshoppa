# Workshoppa

Workshoppa helps manage company workshop projects and turn in materials in Final Fantasy XIV. This fork publishes builds for Dalamud API 15. The original plugin is by Liza Carvelli (vera.lyn).

## Install in Dalamud

1. Run `/xlsettings` and open **Experimental**.
2. Under **Custom Plugin Repositories**, add and enable the URL below, then save.
3. Run `/xlplugins`, search for **Workshoppa**, and select **Install**.

```text
https://raw.githubusercontent.com/Solterax/Workshoppa/main/repo.json
```

If you already have Workshoppa from another repository, uninstall that copy before installing from this feed. Both copies use the same plugin ID.

The packaged ZIP is also available from [GitHub Releases](https://github.com/Solterax/Workshoppa/releases/latest).

## Retainer transfers

Open a retainer's inventory at a summoning bell, then open Workshoppa with `/ws`.

- **Deposit all materials** moves transferable items from your inventory bags, excluding submarine parts.
- **Deposit all submarine parts** moves submersible hulls, sterns, bows and bridges from your bags.
- **Withdraw needed materials** retrieves the shortfall for your current workshop project and queue from the open retainer. The material list shows what you still need after the transfer.

Use **Stop transfer** to stop sending new moves.
