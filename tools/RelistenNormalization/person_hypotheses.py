"""Read-only shared people from directly supported credit occurrences.

Names retrieve observations; they never supply membership. A hypothesis has one
anchor credit and only members directly linked to it by retained same-person
decisions. Different-person evidence blocks conflicting membership. Search hides
redundant subset views, while every anchor ID remains addressable. Overlapping
stars are alternatives: this projection never takes a graph's transitive closure.
"""

from collections import Counter, defaultdict
import hashlib
import json
from urllib.parse import urlencode


MEMBERSHIP_RULE = (
    "Every included credit has direct same-person evidence with the anchor credit. "
    "Matching names do not add other recordings. Conflicting evidence blocks membership; "
    "overlapping hypotheses remain alternatives. These identities are provisional."
)


class PersonHypotheses:
    """One immutable query projection over a directory's surviving comparisons."""

    def __init__(self, db, name_key):
        self.name_key = name_key
        self.credits = {}
        self.relations = []
        self.hypotheses = {}
        self.visible = []
        if not db.execute("SELECT 1 FROM sqlite_master WHERE name='context_relationships'").fetchone():
            return

        self.relations = self._read_comparisons(db)
        self.credits = self._read_credits(db)
        self._build_stars()

    @staticmethod
    def _read_comparisons(db):
        return [dict(row) for row in db.execute("""
            SELECT r.* FROM context_relationships r
            JOIN credits l ON l.id=r.left_credit JOIN credits n ON n.id=r.right_credit
            WHERE l.group_id=r.left_group AND n.group_id=r.right_group
            ORDER BY r.id
        """)]
    @staticmethod
    def _read_credits(db):
        return {row['id']: dict(row) for row in db.execute("""
            WITH members AS (
                SELECT left_credit id FROM context_relationships
                UNION SELECT right_credit FROM context_relationships
            ) SELECT c.*,a.name artist_name FROM members m JOIN credits c ON c.id=m.id
                LEFT JOIN artists a ON a.uuid=c.artist_uuid
        """)}

    def _build_stars(self):
        positive, negative = defaultdict(set), defaultdict(set)
        for relation in self.relations:
            left, right = relation['left_credit'], relation['right_credit']
            if left == right:
                continue
            target = positive if relation['relation'] == 'same_person' else (
                negative if relation['relation'] == 'different_people' else None
            )
            if target is not None:
                target[left].add(right)
                target[right].add(left)

        for anchor in sorted(positive):
            members = {anchor}
            # Stable ordering makes conflicting alternatives reproducible. No
            # candidate can enter through another member's positive relationship.
            for candidate in sorted(positive[anchor] - negative[anchor]):
                if not negative[candidate].intersection(members):
                    members.add(candidate)
            if len(members) < 2:
                continue
            credit = self.credits[anchor]
            identifier = 'person-hypothesis:' + hashlib.sha256(
                json.dumps([credit['source_uuid'], anchor], separators=(',', ':')).encode()
            ).hexdigest()[:32]
            self.hypotheses[identifier] = dict(id=identifier, anchor=anchor, members=frozenset(members))

        # Show the broadest directly supported views. Subset anchor URLs remain
        # valid, so later evidence cannot silently redirect an existing ID.
        seen = []
        for hypothesis in sorted(self.hypotheses.values(), key=lambda h: (-len(h['members']), h['anchor'])):
            if any(hypothesis['members'] <= members for members in seen):
                continue
            self.visible.append(hypothesis)
            seen.append(hypothesis['members'])

    def summary(self, hypothesis):
        credits = [self.credits[identifier] for identifier in hypothesis['members']]
        counts = Counter(credit['name'] for credit in credits)
        names = sorted(counts, key=lambda name: (-counts[name], -len(name), name.casefold(), name))
        aliases = sorted({alias['wording'] for credit in credits for alias in json.loads(credit['aliases'])}, key=str.casefold)
        return dict(id=hypothesis['id'], displayName=names[0], names=names, aliases=aliases,
                    recordingCount=len({credit['source_uuid'] for credit in credits}),
                    artistCount=len({credit['artist_uuid'] for credit in credits}),
                    creditCount=len(credits), status='provisional', acceptedIdentity=False,
                    basis='direct_contextual_support')

    def search(self, query, offset, limit):
        key = self.name_key(query)
        results = [self.summary(hypothesis) for hypothesis in self.visible]
        results = [result for result in results if not key or any(
            key in self.name_key(wording) for wording in result['names'] + result['aliases']
        )]
        results.sort(key=lambda result: (-result['recordingCount'], -result['artistCount'], result['id']))
        return dict(personHypotheses=results[offset:offset + limit], personHypothesesTotal=len(results),
                    personHypothesesOffset=offset, personHypothesesLimit=limit)

    def for_group(self, identifier):
        return [self.summary(hypothesis) for hypothesis in self.visible if any(
            self.credits[credit]['group_id'] == identifier for credit in hypothesis['members']
        )]

    def detail(self, identifier):
        hypothesis = self.hypotheses.get(identifier)
        if hypothesis is None:
            return None
        members = hypothesis['members']
        result = self.summary(hypothesis)
        result.update(anchorCreditId=hypothesis['anchor'], membershipRule=MEMBERSHIP_RULE,
                      support=[], conflicts=[], insufficientEvidence=[], relatedSupport=[],
                      overlappingHypotheses=[self.summary(other) for other in self.visible
                                             if other['id'] != identifier and other['members'] != members
                                             and other['members'].intersection(members)])
        for relation in self.relations:
            left, right = relation['left_credit'], relation['right_credit']
            if left not in members and right not in members:
                continue
            item = dict(id=relation['id'], relation=relation['relation'], reason=relation['reason'],
                        evidence=json.loads(relation['evidence']), leftCreditId=left, rightCreditId=right,
                        leftName=self.credits[left]['name'], rightName=self.credits[right]['name'],
                        evidenceUrl='/api/relationship?' + urlencode(dict(id=relation['id'])))
            target = 'conflicts' if relation['relation'] == 'different_people' else (
                'insufficientEvidence' if relation['relation'] == 'insufficient_evidence' else (
                    'support' if left in members and right in members else 'relatedSupport'
                )
            )
            result[target].append(item)
        return result, [self.credits[credit] for credit in sorted(members)]
