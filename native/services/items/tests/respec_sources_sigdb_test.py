#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""GitHub #150: qualify crafting.respec_sources (sigdb server.d/ and client.d/respec-sources.json) on static copies of the
pinned client (26430625, 5d9ef2f1) and dedicated server (26430253, f0d824be): the files are read, never run.

What it pins: the respec payer (respec/Payer: cost 0 is paid at once, stock mode 2) and its one payment call
(respec/RespecPaid: rules/InventoryPay(tx = rbp+0x50, {one line of respec/item x cost}, player, 1, 0), commit on 0, else
roll back), its only two callers (the unlearn, which undoes the NodeSet on a refusal, and skills/Reset with its pay flag),
the join check's free reset (pay flag 0, so never the payer) and the action's paid reset, the client's two respec checks
(respec/client.NodeCountRet: the node's unlearn is offered when the count reaches its cost; respec/client.AltarCountRet:
the altar's reskill button when the count reaches the points spent), the display-only Rune row (respec/client.HeaderCountRet,
not part of the feature), the UI commands they lead to (0x4f unlearn, 0x4e with skill 0 = reset all), and that a changed
byte at each site turns the feature off (the display row only that symbol)."""
import argparse
import copy
import hashlib
from pathlib import Path
import struct
import sys
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / 'tools/sigport'))
from peimg import Image
from sides import compose
from sigdb_check import Resolver

checks = 0
CLIENT = '5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77'
SERVER = 'f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2'
RUNE = 0x41a792dc


def check(ok, text):
    global checks
    checks += 1
    if not ok:
        raise AssertionError(text)
    print('PASS ' + text, flush=True)


def part(side, name='respec-sources.json'):
    return {n: doc for n, doc, _ in compose(side)}[name]


def doc_for(side):
    respec = part(side)
    symbols = copy.deepcopy(respec['symbols'])
    # rules/InventoryPay is captured by rules.json (rules/UpgradeStoragePaid's rel32), stations/client.UiAvailableCount
    # defined by stations-fill-ui.json: the respec rows only wildcard those calls (kiln-items checks each call's target)
    for name, file in (('rules/UpgradeStoragePaid', 'rules.json'),) + ((('stations/client.UiAvailableCount', 'stations-fill-ui.json'),)
                                                                     if side == 'client' else ()):
        symbols[name] = copy.deepcopy(part(side, file)['symbols'][name])
    want = respec['features']['crafting.respec_sources']
    consts = {k: v for k, v in respec['constants'].items()}
    return {'symbols': symbols, 'constants': copy.deepcopy(consts),
            'features': {'respec': {'symbols': [n for n in want['symbols'] if n in symbols],
                                    'constants': [c for c in want['constants'] if c in consts]}}}


def rel(raw, at):
    return at + 5 + struct.unpack_from('<i', raw, at + 1)[0]


def callers(image, target):
    t, va = image.text, image.text_va
    out = []
    i = t.find(b'\xe8')
    while i != -1:
        if i + 5 <= len(t) and va + i + 5 + struct.unpack_from('<i', t, i + 1)[0] == target:
            out.append(va + i)
        i = t.find(b'\xe8', i + 1)
    return out


def main():
    args = argparse.ArgumentParser(description=__doc__)
    args.add_argument('--client', required=True)
    args.add_argument('--server', required=True)
    args = args.parse_args()
    client, server = Path(args.client), Path(args.server)
    check(hashlib.sha256(client.read_bytes()).hexdigest() == CLIENT, 'qualified client identity (26430625)')
    check(hashlib.sha256(server.read_bytes()).hexdigest() == SERVER, 'qualified dedicated identity (26430253)')
    c, s = Image(client), Image(server)
    # name: (payer, payment return, InventoryPay, rollback, commit, CurrentEntityId, Reset, unlearn, player_skilltree,
    #        the free reset's call, the paid reset's call, the unlearn's call, PointsSpent)
    expect = {
        'server': (s, dict(payer=0xb9230, paid=0xb93c3, pay=0x199c80, rollback=0x1b8310, commit=0x1af860, entity=0x65b870,
                           reset=0xb9a00, unlearn=0xbbab0, tree=0xd4780, free=0xd48a7, reset_call=0xd4944, unlearn_call=0xd4962,
                           spent=0x9222f0)),
        'client': (c, dict(payer=0x2cdce0, paid=0x2cde73, pay=0x3f89e0, rollback=0x418150, commit=0x40fe30, entity=0x9bf9e0,
                           reset=0x2ce370, unlearn=0x2cfb10, tree=0x2e87e0, free=0x2e8907, reset_call=0x2e89a4, unlearn_call=0x2e89c2,
                           spent=0xe7b170)),
    }
    found_client = {}
    for side, (image, a) in expect.items():
        doc = doc_for(side)
        found, consts, features = Resolver(image, doc).run()
        if side == 'client':
            found_client = found
        check(features['respec'], f'{side}: crafting.respec_sources resolves')
        check(found['respec/Payer']['ok'] and found['respec/Payer']['rva'] == a['payer'], f"{side}: respec/Payer = {a['payer']:#x}")
        check(found['respec/RespecPaid']['ok'] and found['respec/RespecPaid']['rva'] == a['paid'],
              f"{side}: respec/RespecPaid = {a['paid']:#x}")
        check(found['rules/InventoryPay']['rva'] == a['pay'] and consts['respec/item']['value'] == RUNE,
              f'{side}: rules/InventoryPay = {a["pay"]:#x}; respec/item = {RUNE:#x} (Runes)')
        raw = image.img
        payer, paid = a['payer'], a['paid']
        # the payer: movzx ebx,dx (cost); test bx,bx; jne; mov al,1 -> return (cost 0 paid, no transaction); the setup's
        # flags 0 and stock mode 2 at [rsp+0x60]
        check(raw[payer + 0x20:payer + 0x2d] == bytes.fromhex('0FB7DA488BF96685DB7507B001') and
              raw[payer + 0x34:payer + 0x3b] == bytes.fromhex('66C744246000' '02'),
              f'{side}: the payer answers paid for cost 0 and opens a stock-mode-2 transaction (bag, then the Magic chests)')
        # its setup: the bases word (setup +0xd8 = rbp+0x38) and the player's position (setup +0xe0 = rbp+0x40) are set,
        # Fuel (setup +0x70 = rbp-0x30) is left 0, as in the blueprint program's (access_from_tx_reads, not access_from_tx)
        body = raw[payer:payer + 0x21a]
        check(bytes.fromhex('48894538') in body and bytes.fromhex('F30F114540') in body and bytes.fromhex('660F7F4DD0') in body,
              f'{side}: the setup has the bases word and the position, and no Fuel accessor')
        check(rel(raw, paid - 5) == a['pay'], f'{side}: respec/RespecPaid follows the call to rules/InventoryPay')
        check(struct.unpack_from('<I', raw, paid - 0x38)[0] == RUNE and raw[paid - 0x40:paid - 0x3c] == bytes.fromhex('895C2454'),
              f'{side}: the cost line is {{respec/item, the cost}} at rsp+0x50 / +0x54')
        check(rel(raw, paid - 0x34) == a['entity'], f'{side}: the payer is world/CurrentEntityId of the program context')
        check(raw[paid - 0x17:paid - 0x13] == bytes.fromhex('488D4D50') and raw[paid - 0xe:paid - 0xb] == bytes.fromhex('448B00') and
              raw[paid - 0xb:paid - 0x5] == bytes.fromhex('41B901000000'),
              f'{side}: InventoryPay(tx = rbp+0x50, lines, the player, multiplier 1, no override)')
        check(raw[paid:paid + 8] == bytes.fromhex('488D4D5085C07409') and rel(raw, paid + 8) == a['rollback'] and
              rel(raw, paid + 0x11) == a['commit'], f'{side}: 0 commits the transaction, anything else rolls it back')
        check(len(callers(image, a['pay'])) >= 2 and callers(image, payer) == sorted(callers(image, payer)) and
              len(callers(image, payer)) == 2, f'{side}: the payer has exactly two callers')
        cs = callers(image, payer)
        check(a['reset'] < cs[0] < a['reset'] + 0x200 and a['unlearn'] < cs[1] < a['unlearn'] + 0x200,
              f'{side}: they are skills/Reset ({a["reset"]:#x}) and the unlearn ({a["unlearn"]:#x})')
        # Reset(iter, dl pay): PointsSpent, then the payer only when the pay byte is set (test sil,sil; je skip)
        r = cs[0]
        check(rel(raw, r - 0x13) == a['spent'] and raw[r - 0x0b:r - 0x6] == bytes.fromhex('4084F6741A') and
              raw[r - 0x6:r - 0x3] == bytes.fromhex('0FB7D0'),
              f'{side}: skills/Reset pays the points spent (PointsSpent) only when its pay flag is set')
        check(raw[r + 5:r + 9] == bytes.fromhex('84C00F84'), f'{side}: a refused payment leaves skills/Reset (no reset)')
        # the unlearn: a refused payment puts the node back (NodeSet with unlock 1)
        u = cs[1]
        check(raw[u + 5:u + 9] == bytes.fromhex('84C07404') and bytes.fromhex('41B001') in raw[u + 9:u + 0x40],
              f'{side}: a refused unlearn payment sets the node again')
        # the join check's reset is free: xor edx,edx right before its call; the action's reset passes !free-skills
        check(rel(raw, a['free']) == a['reset'] and raw[a['free'] - 7:a['free'] - 5] == bytes.fromhex('33D2'),
              f'{side}: the join check calls skills/Reset with pay 0 (free, never the payer)')
        check(rel(raw, a['reset_call']) == a['reset'] and raw[a['reset_call'] - 3:a['reset_call']] == bytes.fromhex('0FB6D0') and
              rel(raw, a['unlearn_call']) == a['unlearn'],
              f'{side}: the action calls skills/Reset with its pay flag and the unlearn')
        # a changed byte at the payment call or the payer's cost test disables the feature
        for at in (paid - 5, payer + 0x26):
            altered = copy.copy(image)
            b = bytearray(raw)
            b[at] = 0x90
            altered.img = bytes(b)
            altered.text = altered.img[altered.text_va:altered.text_va + len(image.text)]
            check(not Resolver(altered, doc).run()[2]['respec'], f'{side}: a changed byte at {at:#x} disables crafting.respec_sources')
    raw = c.img
    avail = 0xd397d0
    check(found_client['stations/client.UiAvailableCount']['rva'] == avail, 'client: stations/client.UiAvailableCount = 0xd397d0')
    node, altar, header = 0xcb7e98, 0xc7eeb4, 0xcb9cb4
    for name, rva in (('respec/client.NodeCountRet', node), ('respec/client.AltarCountRet', altar),
                      ('respec/client.HeaderCountRet', header)):
        check(found_client[name]['ok'] and found_client[name]['rva'] == rva and rel(raw, rva - 5) == avail,
              f'client: {name} = {rva:#x}, the return of a UiAvailableCount call')
    _, consts, _ = Resolver(c, doc_for('client')).run()
    check(consts['respec/client.ui.item']['value'] == 0x44b238 and consts['respec/client.ui.cost']['value'] == 0x44b23c,
          'client: G38UiPlayData respec item +0x44b238, cost +0x44b23c')
    # player_ui writes both: the item from its config, the cost from the points spent
    check(raw[0x2b9763:0x2b9769] == bytes.fromhex('898638B24400') and raw[0x2b98df:0x2b98e5] == bytes.fromhex('89863CB24400'),
          'client: player_ui writes the respec item and its cost')
    # the node details: movzx r14d,[rax+0x1c] (the cost), the item from [r12+0x44b238], mov esi,eax; cmp esi,r14d; jae;
    # the offered unlearn (bl) -> 0xd91830(2, 0)
    check(raw[node - 0x39:node - 0x34] == bytes.fromhex('440FB7701C') and raw[node:node + 2] == bytes.fromhex('8BF0') and
          raw[node + 0x10:node + 0x15] == bytes.fromhex('413BF67348'),
          'client: the node details compare the count (esi) with the node\'s cost (r14d)')
    check(raw[0xcb7ef5 + 7:0xcb7ef5 + 9] == bytes.fromhex('B301') and raw[0xcb81cf:0xcb81d7] == bytes.fromhex('84DB740933D2B102') and
          rel(raw, 0xcb81d7) == 0xd91830, 'client: an affordable node offers action 2 (unlearn)')
    # the tree's action handler: action 2 pressed (0xd68510(2, 1)) -> UI command 0x4f with the node's id
    check(raw[0xd4fe3f:0xd4fe43] == bytes.fromhex('B201B102') and rel(raw, 0xd4fe43) == 0xd68510 and
          raw[0xd4fe91:0xd4fe95] == bytes.fromhex('41C6064F'), 'client: the tree sends the unlearn (UI command 0x4f) on action 2')
    # the altar: cmp eax,[+0x44b23c]; setae met; the button enabled when met and the cost is not 0; its confirmation sends
    # UI command 0x4e (unlock) with skill 0: the input copy turns skill 0 into resetAllSkills
    check(raw[altar:altar + 15] == bytes.fromhex('8B8F3CB244003BC1894D580F934560'), 'client: the altar compares the count with the cost')
    check(raw[0xc7f8f0:0xc7f8f4] == bytes.fromhex('44386560') and raw[0xc7f8fd:0xc7f901] == bytes.fromhex('44396558'),
          'client: the reskill button is enabled by met and a cost above 0')
    check(rel(raw, 0xc7fbaf) == 0xb16300 and raw[0xb16353:0xb16356] == bytes.fromhex('C6074E') and
          raw[0xc7fbb9:0xc7fbbd] == bytes.fromhex('44896004'), 'client: the altar sends UI command 0x4e with skill 0')
    check(raw[0x3b12ce:0x3b12db] == bytes.fromhex('833F000F94C04188867D010000'),
          'client: the input copy sets resetAllSkills when the unlock command names skill 0')
    # the header row: display only, not part of the feature
    check(raw[header:header + 7] == bytes.fromhex('488D4D908945B8'), 'client: the Rune row stores the count it shows')
    names = part('client')['features']['crafting.respec_sources']['symbols']
    check('respec/client.HeaderCountRet' not in names and 'respec/client.NodeCountRet' in names and
          'respec/client.AltarCountRet' in names, 'client: the display row is not one of the feature\'s symbols; the two checks are')
    for at in (node - 5, altar - 5):
        altered = copy.copy(c)
        b = bytearray(raw)
        b[at] = 0x90
        altered.img = bytes(b)
        altered.text = altered.img[altered.text_va:altered.text_va + len(c.text)]
        check(not Resolver(altered, doc_for('client')).run()[2]['respec'],
              f'client: a changed call at {at:#x} disables crafting.respec_sources')
    altered = copy.copy(c)
    b = bytearray(raw)
    b[header + 1] = 0x90
    altered.img = bytes(b)
    altered.text = altered.img[altered.text_va:altered.text_va + len(c.text)]
    f_alt, _, feat_alt = Resolver(altered, doc_for('client')).run()
    check(feat_alt['respec'] and not f_alt['respec/client.HeaderCountRet']['ok'],
          'client: a changed Rune row leaves crafting.respec_sources on; only respec/client.HeaderCountRet does not resolve')
    # the client's sites are client-only UI code
    ui = {'symbols': {k: copy.deepcopy(v) for k, v in part('client')['symbols'].items() if k.startswith('respec/client.')},
          'constants': copy.deepcopy(part('client')['constants']), 'features': {'ui': {'symbols': ['respec/client.NodeCountRet',
          'respec/client.AltarCountRet', 'respec/client.HeaderCountRet']}}}
    check(not Resolver(s, ui).run()[2]['ui'], 'the respec screens\' sites are absent from the dedicated server')
    print(f'respec sources sigdb: {checks} passed, 0 failed')


if __name__ == '__main__':
    main()
