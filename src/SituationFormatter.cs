using System;
using System.Globalization;
using System.Text;

namespace SeaPowerAIAdvisor;

internal static class SituationFormatter
{
    internal static string Format(SituationSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.AppendLine("アドバイザーが参照する状況（外部送信なし）");
        text.AppendLine("取得時刻：" + Time(snapshot.At));
        text.AppendLine("敵の位置・数量は観測時点の情報です。未観測は不明として扱います。");
        text.AppendLine("【自軍艦隊】");
        if (snapshot.Forces.Count == 0) text.AppendLine("艦隊なし");
        foreach (var force in snapshot.Forces)
        {
            text.AppendLine(force.Name + "：" + Position(force.Position) + "／" + (force.Port == null ? "洋上" : "入港中 " + force.Port));
            text.AppendLine("  速力 " + Number(force.SpeedKnots) + " kt／任務 " + Mission(force.Mission));
            foreach (var ship in force.Ships)
                text.AppendLine("  " + ship.Name + "（" + ship.Type + "）：燃料 " + (ship.Nuclear ? "原子力" : Percent(ship.FuelShare)) +
                    "／弾薬 " + (ship.Unarmed ? "非武装" : Percent(ship.AmmoShare!.Value)) + "／損傷 " + Percent(ship.DamageShare));
        }
        text.AppendLine("\n【既知の敵接触】");
        if (snapshot.Contacts.Count == 0) text.AppendLine("現在の観測記録なし（敵が存在しないという意味ではありません）");
        var index = 0;
        foreach (var contact in snapshot.Contacts)
        {
            var label = contact.Kind == "Air" ? "航空接触" : contact.Kind == "Submarine" ? "潜水艦接触" : "水上接触";
            text.AppendLine("接触" + ++index + " " + label + "：" + contact.Count + "／" + (contact.Identified ? "識別済み" : "未識別"));
            text.AppendLine("  最終観測 " + Time(contact.SeenAt) + "／" + Position(contact.Position));
            text.AppendLine("  経過 " + Number((snapshot.At - contact.SeenAt).TotalHours) + "時間／位置の不確かさ 半径 " + Number(contact.UncertaintyNm) + " nm");
        }
        text.AppendLine("\n【敵基地の偵察情報】");
        if (snapshot.Bases.Count == 0) text.AppendLine("なし");
        foreach (var b in snapshot.Bases)
        {
            text.AppendLine(b.Name + "（" + BaseKind(b.Kind) + "）" + Position(b.Position));
            text.AppendLine(b.LandSeenAt.HasValue
                ? "  地上防備 最終観測 " + Time(b.LandSeenAt.Value) + "：砲台 " + b.Batteries + "／SAM " + b.SamSites + "／地上部隊 " + b.GroundUnits
                : "  地上防備：不明（未観測）");
            text.AppendLine(b.ForcesSeenAt.HasValue
                ? "  艦船・航空機 最終観測 " + Time(b.ForcesSeenAt.Value) + "：在港艦船 " + b.Ships + "／航空機 " + b.Aircraft
                : "  在港艦船・航空機：不明（未観測）");
        }
        text.AppendLine("\n【開示済みの命令と作戦】");
        if (snapshot.Orders.Count == 0) text.AppendLine("なし");
        foreach (var order in snapshot.Orders)
        {
            text.AppendLine(order.Title + "（" + OrderStatus(order.Status) + "）");
            text.AppendLine("  提示 " + Time(order.OfferedAt) + (order.Deadline.HasValue ? "／期限 " + Time(order.Deadline.Value) : ""));
            text.AppendLine(order.Briefing);
        }
        text.AppendLine("\n【プレイヤー向け報告 直近" + CampaignSituationReader.ReportLimit + "件まで】");
        if (snapshot.Reports.Count == 0) text.AppendLine("なし");
        foreach (var report in snapshot.Reports) text.AppendLine(Time(report.At) + " " + report.Text);
        return text.ToString();
    }

    private static string Position(MapPosition point) => Number(point.Latitude) + "°, " + Number(point.Longitude) + "°";
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Percent(double value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Time(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
    private static string BaseKind(string kind) => kind switch
    { "AirBase" => "飛行場", "NavalBase" => "海軍基地", "Port" => "港", _ => throw new InvalidOperationException("Unknown base classification: " + kind) };
    private static string OrderStatus(string value) => value switch
    { "Offered" => "受諾待ち", "Active" => "遂行中", "Completed" => "完了", "Failed" => "失敗", "Declined" => "辞退", "Lapsed" => "期限切れ", "Cancelled" => "取消", _ => value };
    private static string Mission(string value) => value switch
    { "None" => "なし", "Patrol" => "哨戒", "Hunt" => "捜索・撃破", "Escort" => "護衛", "Cap" => "戦闘空中哨戒", "Cover" => "掩護", "Search" => "捜索", "Follow" => "追従", "Rendezvous" => "会合", "Join" => "合流", "Invade" => "上陸", "StrikeBase" => "基地攻撃", "Replenish" => "補給", "Refuel" => "給油", "TankerStation" => "給油待機", _ => value };
}
