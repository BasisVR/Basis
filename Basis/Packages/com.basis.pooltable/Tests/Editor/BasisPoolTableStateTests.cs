using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using GameStateData = NetworkingManager.GameStateData;
using Object = UnityEngine.Object;

namespace Basis.Tests.PoolTable
{
    public class BasisPoolTableStateTests
    {
        private const int PhysicsCount = 2;
        private const int TableModelCount = 3;

        private GameObject _go;
        private BilliardsModule _table;
        private NetworkingManager _net;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("PoolTableUnderTest");
            _table = _go.AddComponent<BilliardsModule>();
            _net = _go.AddComponent<NetworkingManager>();
            _table.networkingManager = _net;
            _table.PhysicsManagers = new AdvancedPhysicsManager[PhysicsCount];
            _table.tableModels = new ModelData[TableModelCount];
            typeof(NetworkingManager).GetField("table", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_net, _table);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        private GameStateData SyncState => (GameStateData)typeof(NetworkingManager).GetField("SyncState", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_net);

        private static GameStateData ValidState()
        {
            GameStateData state = new GameStateData();
            state.teamIdSynced = 1;
            state.teamColorSynced = 1;
            state.winningTeamSynced = 2;
            state.gameModeSynced = 4;
            state.physicsSynced = PhysicsCount - 1;
            state.tableModelSynced = TableModelCount - 1;
            return state;
        }

        [Test]
        public void TheHighestValueOfEveryTeamModeAndIndexIsAccepted()
        {
            Assert.IsTrue(ValidState().IsValid(PhysicsCount, TableModelCount));
            Assert.IsTrue(new GameStateData().IsValid(PhysicsCount, TableModelCount), "A fresh state is a valid state.");
        }

        [Test]
        public void AnythingPastTheLastTeamModeOrIndexIsRejected()
        {
            GameStateData state = ValidState();
            state.teamIdSynced = 2;
            Assert.IsFalse(state.IsValid(PhysicsCount, TableModelCount), "teamId indexes the two cues.");

            state = ValidState();
            state.teamColorSynced = 2;
            Assert.IsFalse(state.IsValid(PhysicsCount, TableModelCount), "teamColor indexes the two scorecard counters.");

            state = ValidState();
            state.winningTeamSynced = 3;
            Assert.IsFalse(state.IsValid(PhysicsCount, TableModelCount));

            state = ValidState();
            state.gameModeSynced = 5;
            Assert.IsFalse(state.IsValid(PhysicsCount, TableModelCount), "gameMode indexes the rack layouts.");

            state = ValidState();
            state.physicsSynced = PhysicsCount;
            Assert.IsFalse(state.IsValid(PhysicsCount, TableModelCount), "physics indexes PhysicsManagers.");

            state = ValidState();
            state.tableModelSynced = TableModelCount;
            Assert.IsFalse(state.IsValid(PhysicsCount, TableModelCount), "tableModel indexes tableModels.");
        }

        [Test]
        public void AStateSurvivesItsOwnEncoding()
        {
            GameStateData state = ValidState();
            state.playerIDsSynced = new[] { 7, -1, 300, 65535 };
            state.ballsPSynced[3] = new Vector3(0.25f, -0.5f, 1.75f);
            state.cueBallVSynced = new Vector3(1f, 2f, 3f);
            state.cueBallWSynced = new Vector3(-4f, 5f, -6f);
            state.stateIdSynced = 777;
            state.ballsPocketedSynced = 0xA5A5;
            state.timerStartSynced = -123456;
            state.foulStateSynced = 6;
            state.isTableOpenSynced = true;
            state.gameStateSynced = 2;
            state.turnStateSynced = 1;
            state.timerSynced = 45;
            state.teamsSynced = true;
            state.noGuidelineSynced = true;
            state.noLockingSynced = true;
            state.fourBallScoresSynced = new byte[] { 9, 200 };
            state.fourBallCueBallSynced = 4;
            state.isUrgentSynced = 2;
            state.colorTurnSynced = true;

            GameStateData copy = GameStateData.FromBytes(state.ToBytes(0), 1);

            Assert.IsNotNull(copy);
            foreach (FieldInfo field in typeof(GameStateData).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object before = field.GetValue(state);
                object after = field.GetValue(copy);
                if (before is System.Array array)
                {
                    Assert.That(after, Is.EqualTo(array), field.Name);
                    Assert.That(after, Is.Not.SameAs(array), field.Name + " has to be a copy, not the same array.");
                }
                else
                {
                    Assert.That(after, Is.EqualTo(before), field.Name);
                }
            }
        }

        [Test]
        public void ASaveWithATeamPastTheLastIsIgnored()
        {
            SyncState.teamIdSynced = 2;
            string save = _net._EncodeGameState();
            SyncState.teamIdSynced = 0;
            GameStateData before = SyncState;
            ushort stateId = before.stateIdSynced;

            _net._OnLoadGameState(save);

            Assert.That(SyncState, Is.SameAs(before), "A rejected save must not replace the table state.");
            Assert.That(SyncState.stateIdSynced, Is.EqualTo(stateId));
            Assert.That(SyncState.teamIdSynced, Is.EqualTo(0));
        }

        [Test]
        public void ASaveWithAModePastTheLastIsIgnored()
        {
            SyncState.gameModeSynced = 9;
            string save = _net._EncodeGameState();
            SyncState.gameModeSynced = 0;
            GameStateData before = SyncState;

            _net._OnLoadGameState(save);

            Assert.That(SyncState, Is.SameAs(before));
            Assert.That(SyncState.gameModeSynced, Is.EqualTo(0));
        }

        [Test]
        public void AValidSaveIsAppliedAsANewState()
        {
            SyncState.teamIdSynced = 1;
            SyncState.teamColorSynced = 1;
            SyncState.gameModeSynced = 4;
            SyncState.ballsPSynced[5] = new Vector3(0.1f, 0.2f, 0.3f);
            SyncState.fourBallScoresSynced[0] = 5;
            SyncState.fourBallScoresSynced[1] = 7;
            string save = _net._EncodeGameState();

            SyncState.teamIdSynced = 0;
            SyncState.teamColorSynced = 0;
            SyncState.gameModeSynced = 0;
            SyncState.ballsPSynced[5] = Vector3.zero;
            SyncState.fourBallScoresSynced[0] = 0;
            SyncState.fourBallScoresSynced[1] = 0;
            SyncState.tableModelSynced = TableModelCount - 1;
            GameStateData before = SyncState;
            ushort stateId = before.stateIdSynced;

            _net._OnLoadGameState(save);

            Assert.That(SyncState, Is.Not.SameAs(before));
            Assert.That(SyncState.stateIdSynced, Is.EqualTo((ushort)(stateId + 1)));
            Assert.That(SyncState.teamIdSynced, Is.EqualTo(1));
            Assert.That(SyncState.teamColorSynced, Is.EqualTo(1));
            Assert.That(SyncState.gameModeSynced, Is.EqualTo(4));
            Assert.That(SyncState.ballsPSynced[5], Is.EqualTo(new Vector3(0.1f, 0.2f, 0.3f)));
            Assert.That(SyncState.fourBallScoresSynced, Is.EqualTo(new byte[] { 5, 7 }));
            Assert.That(SyncState.tableModelSynced, Is.EqualTo(TableModelCount - 1), "Fields a save does not carry keep the table's own values.");
            Assert.That(SyncState.isUrgentSynced, Is.EqualTo(2), "A loaded save goes out as an urgent state.");
        }

        [TestCase(300)]
        [TestCase(1023)]
        public void ALateJoinerFarIntoAGameStillRecordsItsFirstState(int stateId)
        {
            PracticeManager practice = _go.AddComponent<PracticeManager>();
            _table.practiceManager = practice;
            practice._Init(_table);
            SyncState.stateIdSynced = (ushort)stateId;

            Assert.DoesNotThrow(() => practice._Record());

            object[] history = (object[])typeof(PracticeManager).GetField("history", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(practice);
            Assert.That(history.Length, Is.GreaterThan(stateId));
            Assert.That(history[stateId], Is.Not.Null);
        }
    }
}
